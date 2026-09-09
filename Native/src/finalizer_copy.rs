//! Packet-only death replay export. Keep the preceding GOP as hidden decoder preroll;
//! MP4 edit lists hide negative timestamps, so a non-keyframe trim does not flash
//! earlier gameplay or delay audio. Never round the visible cut to a keyframe.
use std::path::Path;

use ffmpeg::{Dictionary, Packet, codec, encoder, format, media};
use ffmpeg_next as ffmpeg;

use crate::finalizer::{FinalizeClip, FinalizeError};

const MAX_PREROLL_BYTES: usize = 32 * 1024 * 1024;

fn continuous_range(clips: &[FinalizeClip]) -> Option<(f64, f64)> {
    let first = clips.first()?;
    let mut end = first.start_seconds + first.duration_seconds;
    for clip in &clips[1..] {
        // Room/music metadata boundaries are not edits. Pauses, cuts and overlaps
        // require the exact renderer (including its existing transition policy).
        if clip.source != first.source || (clip.start_seconds - end).abs() > 1e-7 {
            return None;
        }
        end = clip.start_seconds + clip.duration_seconds;
    }
    Some((first.start_seconds, end))
}

pub fn copy_video(clips: &[FinalizeClip], destination: &Path) -> Result<bool, FinalizeError> {
    let Some((start, end)) = continuous_range(clips) else {
        return Ok(false);
    };
    let source_path = Path::new(&clips[0].source);
    let mut input = format::input(source_path).map_err(|source| FinalizeError::OpenInput {
        path: source_path.to_owned(),
        source,
    })?;
    let video = input
        .streams()
        .best(media::Type::Video)
        .ok_or_else(|| FinalizeError::MissingVideo(source_path.to_owned()))?;
    let index = video.index();
    let time_base = video.time_base();
    let parameters = video.parameters();
    let frame_rate = video.avg_frame_rate();
    // The capture encoder uses H.264 without B frames. Do not assume arbitrary
    // codecs or reordered packets have the same safe end-of-GOP trimming rules.
    if parameters.id() != codec::Id::H264 {
        return Ok(false);
    }
    drop(video);
    let seek = (start * 1_000_000.0).floor() as i64;
    if seek > 0 && input.seek(seek, ..seek).is_err() {
        return Ok(false);
    }
    let tick = f64::from(time_base);
    let origin = (start / tick).round() as i64;
    let end_tick = (end / tick).round() as i64;
    let mut output = format::output(destination).map_err(|source| FinalizeError::CreateOutput {
        path: destination.to_owned(),
        source,
    })?;
    {
        let mut stream = output
            .add_stream(encoder::find(codec::Id::None))
            .map_err(|source| FinalizeError::AddStream {
                encoder: "packet copy".into(),
                source,
            })?;
        stream.set_parameters(parameters);
        stream.set_time_base(time_base);
        stream.set_avg_frame_rate(frame_rate);
        unsafe { (*stream.parameters().as_mut_ptr()).codec_tag = 0 };
    }
    let mut options = Dictionary::new();
    options.set("avoid_negative_ts", "disabled");
    options.set("use_editlist", "1");
    output
        .write_header_with(options)
        .map_err(FinalizeError::Header)?;
    let output_time_base = output.stream(0).unwrap().time_base();
    let mut preroll = Vec::new();
    let mut preroll_bytes = 0;
    let mut started = false;
    let mut last_timestamp = None;
    for (stream, mut packet) in input.packets() {
        if stream.index() != index {
            continue;
        }
        let (Some(pts), Some(dts)) = (packet.pts(), packet.dts()) else {
            return Ok(false);
        };
        if pts != dts || last_timestamp.is_some_and(|previous| dts <= previous) {
            return Ok(false);
        }
        // A demuxer/index that seeks past the requested start is not safe
        // to copy: fall back rather than silently shortening the replay.
        if last_timestamp.is_none() && seek > 0 && pts > origin {
            return Ok(false);
        }
        last_timestamp = Some(dts);
        if pts >= end_tick {
            break;
        }
        if !started {
            if packet.is_key() {
                preroll.clear();
                preroll_bytes = 0;
            }
            if preroll.is_empty() && !packet.is_key() {
                return Ok(false);
            }
            preroll_bytes += packet.size();
            if preroll_bytes > MAX_PREROLL_BYTES {
                return Ok(false);
            }
            let visible = pts >= origin;
            preroll.push(packet);
            if !visible {
                continue;
            }
            started = true;
            for mut packet in preroll.drain(..) {
                write_packet(
                    &mut packet,
                    origin,
                    end_tick,
                    time_base,
                    output_time_base,
                    &mut output,
                )?;
            }
        } else {
            write_packet(
                &mut packet,
                origin,
                end_tick,
                time_base,
                output_time_base,
                &mut output,
            )?;
        }
    }
    if !started {
        return Ok(false);
    }
    output.write_trailer().map_err(FinalizeError::Trailer)?;
    Ok(true)
}

fn write_packet(
    packet: &mut Packet,
    origin: i64,
    end: i64,
    input_time_base: ffmpeg::Rational,
    output_time_base: ffmpeg::Rational,
    output: &mut format::context::Output,
) -> Result<(), FinalizeError> {
    let pts = packet.pts().unwrap();
    if packet.duration() > 0 {
        packet.set_duration(packet.duration().min(end - pts));
    }
    packet.set_pts(Some(pts - origin));
    packet.set_dts(Some(packet.dts().unwrap() - origin));
    packet.rescale_ts(input_time_base, output_time_base);
    packet.set_stream(0);
    packet.set_position(-1);
    packet
        .write_interleaved(output)
        .map_err(FinalizeError::Packet)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn clip(start: f64, duration: f64) -> FinalizeClip {
        serde_json::from_value(serde_json::json!({
            "source":"run.mkv", "start_seconds":start, "duration_seconds":duration
        }))
        .unwrap()
    }

    #[test]
    fn only_continuous_ranges_can_reuse_packets() {
        assert_eq!(continuous_range(&[]), None);
        assert_eq!(continuous_range(&[clip(12.3, 10.)]), Some((12.3, 22.3)));
        let mut room = clip(13., 2.);
        room.bgm_follows_video = true;
        room.seamless_from_previous = true;
        assert_eq!(continuous_range(&[clip(12., 1.), room]), Some((12., 15.)));
        assert_eq!(continuous_range(&[clip(0., 1.), clip(1.001, 1.)]), None);
        assert_eq!(continuous_range(&[clip(0., 1.), clip(0.999, 1.)]), None);
        let mut other = clip(1., 1.);
        other.source = "other.mkv".into();
        assert_eq!(continuous_range(&[clip(0., 1.), other]), None);
    }
}
