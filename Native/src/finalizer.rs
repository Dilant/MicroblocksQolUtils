use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

use ffmpeg::{Packet, Rational, codec, encoder, format, frame, media, software};
use ffmpeg_next as ffmpeg;
use serde::Deserialize;
use thiserror::Error;

use crate::encoder::{
    encoder_candidates, encoder_options, even_dimension, pixel_format_for_encoder,
};
use crate::finalizer_audio;

const CROSSFADE_SECONDS: f64 = 0.25;
pub(crate) const CUT_GAP_SECONDS: f64 = 0.10;

#[derive(Debug, Deserialize)]
#[serde(default)]
pub struct FinalizePlan {
    pub clips: Vec<FinalizeClip>,
    pub output_path: String,
    pub encoder: String,
    pub bitrate_kbps: u32,
    pub fps: u32,
    pub reconstruct_bgm: bool,
    pub remove_freeze_frames: bool,
    /// Death replays can reuse the live encoder's packets instead of encoding twice.
    pub prefer_video_copy: bool,
    pub bgm_event_map_file: String,
}

impl Default for FinalizePlan {
    fn default() -> Self {
        Self {
            clips: Vec::new(),
            output_path: String::new(),
            encoder: "auto".to_owned(),
            bitrate_kbps: 12_000,
            fps: 60,
            reconstruct_bgm: false,
            remove_freeze_frames: false,
            prefer_video_copy: false,
            bgm_event_map_file: String::new(),
        }
    }
}

#[derive(Debug, Clone, Deserialize)]
pub struct FinalizeClip {
    pub source: String,
    pub start_seconds: f64,
    pub duration_seconds: f64,
    #[serde(default)]
    pub music_event: String,
    #[serde(default)]
    pub music_timeline_milliseconds: i64,
    #[serde(default)]
    pub seamless_from_previous: bool,
    /// This room needs the original music/video phase, not a continuous post-edit cursor.
    #[serde(default)]
    pub bgm_follows_video: bool,
}

#[derive(Debug, Clone, Copy, PartialEq)]
struct FreezeSegment {
    start_seconds: f64,
    end_seconds: f64,
}

const FREEZE_GAP_MULTIPLIER: f64 = 1.5;

#[derive(Debug, Clone, Copy)]
pub(crate) struct TimelineClipLayout {
    pub output_start_seconds: f64,
    pub fade_in_seconds: f64,
    pub fade_out_seconds: f64,
}

pub(crate) fn timeline_layout(clips: &[FinalizeClip]) -> Vec<TimelineClipLayout> {
    let mut result = Vec::with_capacity(clips.len());
    let mut output_start_seconds = 0.0;
    for (index, clip) in clips.iter().enumerate() {
        let fade_in_seconds = index
            .checked_sub(1)
            .map(|previous| transition_duration(clips, previous))
            .unwrap_or(0.0);
        let fade_out_seconds = transition_duration(clips, index);
        result.push(TimelineClipLayout {
            output_start_seconds,
            fade_in_seconds,
            fade_out_seconds,
        });
        output_start_seconds += clip.duration_seconds - fade_out_seconds;
    }
    result
}

fn transition_duration(clips: &[FinalizeClip], index: usize) -> f64 {
    let Some(current) = clips.get(index) else {
        return 0.0;
    };
    let Some(next) = clips.get(index + 1) else {
        return 0.0;
    };
    if next.seamless_from_previous {
        return 0.0;
    }
    let source_gap = next.start_seconds - (current.start_seconds + current.duration_seconds);
    if source_gap <= CUT_GAP_SECONDS {
        return 0.0;
    }
    CROSSFADE_SECONDS
        .min(current.duration_seconds * 0.5)
        .min(next.duration_seconds * 0.5)
}

#[derive(Debug, Error)]
pub enum FinalizeError {
    #[error("finalize plan has no clips")]
    Empty,
    #[error("all timeline clips must reference the same continuous recording")]
    MultipleSources,
    #[error("timeline clips must be ordered by nondecreasing source time")]
    OutOfOrder,
    #[error("invalid clip range start={start} duration={duration}")]
    Range { start: f64, duration: f64 },
    #[error("invalid finalizer frame rate {0}")]
    FrameRate(u32),
    #[error("invalid finalizer bitrate {0} kbps")]
    Bitrate(u32),
    #[error("output path has no parent: {0}")]
    MissingParent(PathBuf),
    #[error("cannot create output directory {path}: {source}")]
    CreateDirectory {
        path: PathBuf,
        source: std::io::Error,
    },
    #[error("cannot open input {path}: {source}")]
    OpenInput {
        path: PathBuf,
        source: ffmpeg::Error,
    },
    #[error("input has no video stream: {0}")]
    MissingVideo(PathBuf),
    #[error("cannot open video decoder: {0}")]
    Decoder(ffmpeg::Error),
    #[error("video decoder rejected a packet: {0}")]
    SendPacket(ffmpeg::Error),
    #[error("video decoder could not be flushed: {0}")]
    FlushDecoder(ffmpeg::Error),
    #[error("no decoded frame intersects the retained timeline")]
    NoFrames,
    #[error("no usable H.264 encoder was found; tried {0}")]
    NoEncoder(String),
    #[error("cannot create output {path}: {source}")]
    CreateOutput {
        path: PathBuf,
        source: ffmpeg::Error,
    },
    #[error("cannot add the {encoder} output stream: {source}")]
    AddStream {
        encoder: String,
        source: ffmpeg::Error,
    },
    #[error("cannot open the {encoder} output encoder: {source}")]
    OpenEncoder {
        encoder: String,
        source: ffmpeg::Error,
    },
    #[error("cannot write output header: {0}")]
    Header(ffmpeg::Error),
    #[error("cannot convert decoded frame: {0}")]
    Convert(ffmpeg::Error),
    #[error("output encoder rejected a frame: {0}")]
    SendFrame(ffmpeg::Error),
    #[error("cannot write output packet: {0}")]
    Packet(ffmpeg::Error),
    #[error("cannot flush output encoder: {0}")]
    FlushEncoder(ffmpeg::Error),
    #[error("cannot write output trailer: {0}")]
    Trailer(ffmpeg::Error),
    #[error("cannot replace final output {path}: {source}")]
    Replace {
        path: PathBuf,
        source: std::io::Error,
    },
    #[error("cannot finalize recording audio: {0}")]
    Audio(#[from] finalizer_audio::AudioFinalizeError),
}

pub fn finalize(plan: &FinalizePlan) -> Result<(), FinalizeError> {
    finalize_with_progress(plan, |_| {})
}

pub fn finalize_with_progress(
    plan: &FinalizePlan,
    mut report_progress: impl FnMut(f32),
) -> Result<(), FinalizeError> {
    validate_plan(plan)?;
    report_progress(0.0);
    ffmpeg::init().map_err(|source| FinalizeError::OpenInput {
        path: PathBuf::from("FFmpeg initialization"),
        source,
    })?;

    let output_path = Path::new(&plan.output_path);
    let parent = output_path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .ok_or_else(|| FinalizeError::MissingParent(output_path.to_owned()))?;
    fs::create_dir_all(parent).map_err(|source| FinalizeError::CreateDirectory {
        path: parent.to_owned(),
        source,
    })?;
    let video_temporary = working_path(output_path, "video", "mp4");
    let audio_temporary = working_path(output_path, "audio", "m4a");
    let mixed_pcm = working_path(output_path, "mix", "f32");
    let mux_temporary = temporary_output_path(output_path);
    for path in [
        &video_temporary,
        &audio_temporary,
        &mixed_pcm,
        &mux_temporary,
    ] {
        let _ = fs::remove_file(path);
    }

    let source_path = Path::new(&plan.clips[0].source);
    let effective_clips = if plan.remove_freeze_frames {
        let freezes = detect_freeze_segments(source_path, plan.fps)?;
        remove_freeze_segments(&plan.clips, &freezes)
    } else {
        plan.clips.iter().map(Clone::clone).collect()
    };
    if effective_clips.is_empty() {
        return Err(FinalizeError::NoFrames);
    }
    let copied = if plan.prefer_video_copy && !plan.remove_freeze_frames {
        match crate::finalizer_copy::copy_video(&effective_clips, &video_temporary) {
            Ok(copied) => copied,
            Err(error) => {
                eprintln!(
                    "[mqol] Death replay packet copy unavailable; using exact transcode: {error}"
                );
                false
            }
        }
    } else {
        false
    };
    if copied {
        eprintln!("[mqol] Death replay reused captured video without re-encoding");
    } else {
        transcode_video(
            plan,
            &effective_clips,
            &video_temporary,
            &mut report_progress,
        )?;
    }
    report_progress(0.85);

    let sidecar = PathBuf::from(format!("{}.sfxchunks", source_path.display()));
    let bgm_map = (plan.reconstruct_bgm && !plan.bgm_event_map_file.trim().is_empty())
        .then(|| Path::new(plan.bgm_event_map_file.trim()));
    let has_audio = finalizer_audio::build_audio_track(
        &sidecar,
        &effective_clips,
        &mixed_pcm,
        &audio_temporary,
        plan.reconstruct_bgm,
        bgm_map,
    )?;
    report_progress(0.96);
    if has_audio {
        finalizer_audio::mux_video_and_audio(&video_temporary, &audio_temporary, &mux_temporary)?;
    }
    report_progress(0.99);
    fs::remove_file(output_path).ok();
    let completed = if has_audio {
        &mux_temporary
    } else {
        &video_temporary
    };
    fs::rename(completed, output_path).map_err(|source| FinalizeError::Replace {
        path: output_path.to_owned(),
        source,
    })?;
    for path in [
        &video_temporary,
        &audio_temporary,
        &mixed_pcm,
        &mux_temporary,
    ] {
        let _ = fs::remove_file(path);
    }
    report_progress(1.0);
    Ok(())
}

fn transcode_video(
    plan: &FinalizePlan,
    effective_clips: &[FinalizeClip],
    video_temporary: &Path,
    report_progress: &mut impl FnMut(f32),
) -> Result<(), FinalizeError> {
    let source_path = Path::new(&plan.clips[0].source);
    let mut input = format::input(source_path).map_err(|source| FinalizeError::OpenInput {
        path: source_path.to_owned(),
        source,
    })?;
    let video = input
        .streams()
        .best(media::Type::Video)
        .ok_or_else(|| FinalizeError::MissingVideo(source_path.to_owned()))?;
    let stream_index = video.index();
    let input_time_base = video.time_base();
    let mut decoder = codec::context::Context::from_parameters(video.parameters())
        .and_then(|context| context.decoder().video())
        .map_err(FinalizeError::Decoder)?;
    drop(video);

    let mut selection = TimelineSelection::new(effective_clips);
    let total_duration = timeline_layout(effective_clips)
        .last()
        .zip(effective_clips.last())
        .map(|(layout, clip)| layout.output_start_seconds + clip.duration_seconds)
        .unwrap_or(1.0);
    let mut output = None;
    let mut decoded = frame::Video::empty();
    for (stream, packet) in input.packets() {
        if stream.index() != stream_index {
            continue;
        }
        decoder
            .send_packet(&packet)
            .map_err(FinalizeError::SendPacket)?;
        drain_decoder(
            &mut decoder,
            &mut decoded,
            input_time_base,
            plan,
            video_temporary,
            &mut selection,
            &mut output,
            total_duration,
            report_progress,
        )?;
        if selection.finished() {
            break;
        }
    }
    if !selection.finished() {
        decoder.send_eof().map_err(FinalizeError::FlushDecoder)?;
        drain_decoder(
            &mut decoder,
            &mut decoded,
            input_time_base,
            plan,
            video_temporary,
            &mut selection,
            &mut output,
            total_duration,
            report_progress,
        )?;
    }

    let mut output = output.ok_or(FinalizeError::NoFrames)?;
    output.finish()?;
    Ok(())
}

fn validate_plan(plan: &FinalizePlan) -> Result<(), FinalizeError> {
    if plan.clips.is_empty() {
        return Err(FinalizeError::Empty);
    }
    if !(1..=240).contains(&plan.fps) {
        return Err(FinalizeError::FrameRate(plan.fps));
    }
    if !(100..=200_000).contains(&plan.bitrate_kbps) {
        return Err(FinalizeError::Bitrate(plan.bitrate_kbps));
    }
    let source = &plan.clips[0].source;
    let mut previous_start = -1.0;
    for clip in &plan.clips {
        if !clip.source.eq_ignore_ascii_case(source) {
            return Err(FinalizeError::MultipleSources);
        }
        if !clip.start_seconds.is_finite()
            || clip.start_seconds < 0.0
            || !clip.duration_seconds.is_finite()
            || clip.duration_seconds <= 0.0
        {
            return Err(FinalizeError::Range {
                start: clip.start_seconds,
                duration: clip.duration_seconds,
            });
        }
        if clip.start_seconds < previous_start {
            return Err(FinalizeError::OutOfOrder);
        }
        previous_start = clip.start_seconds;
    }
    Ok(())
}

fn detect_freeze_segments(path: &Path, fps: u32) -> Result<Vec<FreezeSegment>, FinalizeError> {
    let mut input = format::input(path).map_err(|source| FinalizeError::OpenInput {
        path: path.to_owned(),
        source,
    })?;
    let video = input
        .streams()
        .best(media::Type::Video)
        .ok_or_else(|| FinalizeError::MissingVideo(path.to_owned()))?;
    let stream_index = video.index();
    let time_base = video.time_base();
    let mut decoder = codec::context::Context::from_parameters(video.parameters())
        .and_then(|context| context.decoder().video())
        .map_err(FinalizeError::Decoder)?;
    drop(video);

    let mut decoded = frame::Video::empty();
    let mut previous = None;
    let mut segments = Vec::new();
    let threshold = (1.0 / f64::from(fps)).mul_add(FREEZE_GAP_MULTIPLIER, 0.0);
    let mut observe = |decoded: &frame::Video| {
        let Some(timestamp) = decoded.timestamp() else {
            return;
        };
        let current = timestamp as f64 * f64::from(time_base);
        if let Some(previous) = previous {
            let gap = current - previous;
            if gap > threshold {
                let start = previous + 1.0 / f64::from(fps);
                if current > start {
                    segments.push(FreezeSegment {
                        start_seconds: start,
                        end_seconds: current,
                    });
                }
            }
        }
        previous = Some(current);
    };
    for (stream, packet) in input.packets() {
        if stream.index() != stream_index {
            continue;
        }
        decoder
            .send_packet(&packet)
            .map_err(FinalizeError::SendPacket)?;
        while decoder.receive_frame(&mut decoded).is_ok() {
            observe(&decoded);
        }
    }
    decoder.send_eof().map_err(FinalizeError::FlushDecoder)?;
    while decoder.receive_frame(&mut decoded).is_ok() {
        observe(&decoded);
    }
    Ok(segments)
}

fn remove_freeze_segments(clips: &[FinalizeClip], freezes: &[FreezeSegment]) -> Vec<FinalizeClip> {
    let mut result = Vec::with_capacity(clips.len());
    for clip in clips {
        let clip_end = clip.start_seconds + clip.duration_seconds;
        let mut cursor = clip.start_seconds;
        let mut retained: f64 = 0.0;
        let mut first_piece = true;
        for freeze in freezes {
            if freeze.end_seconds <= clip.start_seconds {
                continue;
            }
            if freeze.start_seconds >= clip_end {
                break;
            }
            let start = freeze.start_seconds.max(clip.start_seconds);
            if start > cursor {
                let duration = start - cursor;
                result.push(FinalizeClip {
                    bgm_follows_video: clip.bgm_follows_video,
                    source: clip.source.clone(),
                    start_seconds: cursor,
                    duration_seconds: duration,
                    music_event: clip.music_event.clone(),
                    music_timeline_milliseconds: clip.music_timeline_milliseconds
                        + (if clip.bgm_follows_video {
                            cursor - clip.start_seconds
                        } else {
                            retained
                        } * 1_000.0)
                            .round() as i64,
                    seamless_from_previous: if first_piece {
                        clip.seamless_from_previous
                    } else {
                        true
                    },
                });
                retained += duration;
                first_piece = false;
            }
            cursor = cursor.max(freeze.end_seconds.min(clip_end));
        }
        if cursor < clip_end {
            let duration = clip_end - cursor;
            result.push(FinalizeClip {
                bgm_follows_video: clip.bgm_follows_video,
                source: clip.source.clone(),
                start_seconds: cursor,
                duration_seconds: duration,
                music_event: clip.music_event.clone(),
                music_timeline_milliseconds: clip.music_timeline_milliseconds
                    + (if clip.bgm_follows_video {
                        cursor - clip.start_seconds
                    } else {
                        retained
                    } * 1_000.0)
                        .round() as i64,
                seamless_from_previous: if first_piece {
                    clip.seamless_from_previous
                } else {
                    true
                },
            });
        }
    }
    result
}

#[allow(clippy::too_many_arguments)]
fn drain_decoder(
    decoder: &mut ffmpeg::decoder::Video,
    decoded: &mut frame::Video,
    input_time_base: Rational,
    plan: &FinalizePlan,
    output_path: &Path,
    selection: &mut TimelineSelection<'_>,
    output: &mut Option<TimelineOutput>,
    total_duration: f64,
    report_progress: &mut impl FnMut(f32),
) -> Result<(), FinalizeError> {
    while decoder.receive_frame(decoded).is_ok() {
        let Some(timestamp) = decoded.timestamp() else {
            continue;
        };
        let source_seconds = timestamp as f64 * f64::from(input_time_base);
        let Some(mapped) = selection.map(source_seconds) else {
            continue;
        };
        if output.is_none() {
            *output = Some(TimelineOutput::create(plan, output_path, decoded)?);
        }
        output
            .as_mut()
            .expect("output initialized above")
            .encode(decoded, mapped)?;
        report_progress(((mapped.output_seconds / total_duration) * 0.85).clamp(0.0, 0.85) as f32);
    }
    Ok(())
}

struct TimelineSelection<'a> {
    clips: &'a [FinalizeClip],
    layout: Vec<TimelineClipLayout>,
    index: usize,
}

#[derive(Debug, Clone, Copy, PartialEq)]
enum TimelineBlend {
    Normal,
    Outgoing,
    Incoming { progress: f64 },
}

#[derive(Debug, Clone, Copy, PartialEq)]
struct MappedFrame {
    output_seconds: f64,
    blend: TimelineBlend,
}

impl<'a> TimelineSelection<'a> {
    fn new(clips: &'a [FinalizeClip]) -> Self {
        Self {
            clips,
            layout: timeline_layout(clips),
            index: 0,
        }
    }

    fn map(&mut self, source_seconds: f64) -> Option<MappedFrame> {
        while let Some(clip) = self.clips.get(self.index) {
            let end = clip.start_seconds + clip.duration_seconds;
            if source_seconds < end {
                break;
            }
            self.index += 1;
        }
        let clip = self.clips.get(self.index)?;
        if source_seconds < clip.start_seconds {
            return None;
        }
        let layout = self.layout[self.index];
        let local_seconds = source_seconds - clip.start_seconds;
        let blend = if layout.fade_in_seconds > 0.0 && local_seconds < layout.fade_in_seconds {
            TimelineBlend::Incoming {
                progress: (local_seconds / layout.fade_in_seconds).clamp(0.0, 1.0),
            }
        } else if layout.fade_out_seconds > 0.0
            && local_seconds >= clip.duration_seconds - layout.fade_out_seconds
        {
            TimelineBlend::Outgoing
        } else {
            TimelineBlend::Normal
        };
        Some(MappedFrame {
            output_seconds: layout.output_start_seconds + local_seconds,
            blend,
        })
    }

    fn finished(&self) -> bool {
        self.index >= self.clips.len()
    }
}

struct TimelineOutput {
    output: format::context::Output,
    encoder: encoder::video::Encoder,
    scaler: software::scaling::Context,
    converted: frame::Video,
    output_width: u32,
    output_height: u32,
    input_format: ffmpeg::format::Pixel,
    input_width: u32,
    input_height: u32,
    pixel_format: ffmpeg::format::Pixel,
    stream_index: usize,
    encoder_time_base: Rational,
    stream_time_base: Rational,
    fps: u32,
    last_pts: i64,
    pending_outgoing: BTreeMap<i64, frame::Video>,
    finished: bool,
}

impl TimelineOutput {
    fn create(
        plan: &FinalizePlan,
        path: &Path,
        first: &frame::Video,
    ) -> Result<Self, FinalizeError> {
        let mut failures = Vec::new();
        for name in encoder_candidates(&plan.encoder) {
            let _ = fs::remove_file(path);
            match Self::try_create(plan, path, first, name) {
                Ok(output) => return Ok(output),
                Err(error) => failures.push(format!("{name}: {error}")),
            }
        }
        Err(FinalizeError::NoEncoder(failures.join("; ")))
    }

    fn try_create(
        plan: &FinalizePlan,
        path: &Path,
        first: &frame::Video,
        encoder_name: &str,
    ) -> Result<Self, FinalizeError> {
        let codec = encoder::find_by_name(encoder_name).ok_or_else(|| {
            FinalizeError::NoEncoder(format!("{encoder_name} is absent from this FFmpeg build"))
        })?;
        let pixel_format = pixel_format_for_encoder(encoder_name);
        let output_width = even_dimension(first.width());
        let output_height = even_dimension(first.height());
        let encoder_time_base = Rational(1, plan.fps as i32);
        let mut output = format::output(path).map_err(|source| FinalizeError::CreateOutput {
            path: path.to_owned(),
            source,
        })?;
        let global_header = output
            .format()
            .flags()
            .contains(format::Flags::GLOBAL_HEADER);
        let mut video = codec::context::Context::new_with_codec(codec)
            .encoder()
            .video()
            .map_err(|source| FinalizeError::OpenEncoder {
                encoder: encoder_name.to_owned(),
                source,
            })?;
        video.set_width(output_width);
        video.set_height(output_height);
        video.set_format(pixel_format);
        video.set_time_base(encoder_time_base);
        video.set_frame_rate(Some(Rational(plan.fps as i32, 1)));
        video.set_bit_rate(plan.bitrate_kbps as usize * 1_000);
        video.set_gop(plan.fps.saturating_mul(2));
        video.set_max_b_frames(0);
        if global_header {
            video.set_flags(codec::Flags::GLOBAL_HEADER);
        }
        let opened = video
            .open_as_with(codec, encoder_options(encoder_name))
            .map_err(|source| FinalizeError::OpenEncoder {
                encoder: encoder_name.to_owned(),
                source,
            })?;
        let stream_index;
        {
            let mut stream =
                output
                    .add_stream(codec)
                    .map_err(|source| FinalizeError::AddStream {
                        encoder: encoder_name.to_owned(),
                        source,
                    })?;
            stream.set_time_base(encoder_time_base);
            stream.set_avg_frame_rate(Rational(plan.fps as i32, 1));
            stream.set_parameters(&opened);
            stream_index = stream.index();
        }
        output.write_header().map_err(FinalizeError::Header)?;
        let stream_time_base = output
            .stream(stream_index)
            .expect("newly-added output stream disappeared")
            .time_base();
        let scaler = software::scaling::Context::get(
            first.format(),
            first.width(),
            first.height(),
            pixel_format,
            output_width,
            output_height,
            software::scaling::Flags::BILINEAR,
        )
        .map_err(FinalizeError::Convert)?;
        Ok(Self {
            output,
            encoder: opened,
            scaler,
            converted: frame::Video::new(pixel_format, output_width, output_height),
            output_width,
            output_height,
            input_format: first.format(),
            input_width: first.width(),
            input_height: first.height(),
            pixel_format,
            stream_index,
            encoder_time_base,
            stream_time_base,
            fps: plan.fps,
            last_pts: -1,
            pending_outgoing: BTreeMap::new(),
            finished: false,
        })
    }

    fn encode(&mut self, source: &frame::Video, mapped: MappedFrame) -> Result<(), FinalizeError> {
        if mapped.blend == TimelineBlend::Normal {
            self.flush_pending_outgoing()?;
        }
        if source.format() != self.input_format
            || source.width() != self.input_width
            || source.height() != self.input_height
        {
            self.input_format = source.format();
            self.input_width = source.width();
            self.input_height = source.height();
            self.scaler.cached(
                source.format(),
                source.width(),
                source.height(),
                self.pixel_format,
                self.output_width,
                self.output_height,
                software::scaling::Flags::BILINEAR,
            );
        }
        // QSV and other asynchronous encoders can still own the previous frame.
        // This also preserves images retained in the crossfade queue.
        let writable = unsafe { ffmpeg::ffi::av_frame_make_writable(self.converted.as_mut_ptr()) };
        if writable < 0 {
            return Err(FinalizeError::Convert(ffmpeg::Error::from(writable)));
        }
        self.scaler
            .run(source, &mut self.converted)
            .map_err(FinalizeError::Convert)?;
        let timestamp = (mapped.output_seconds * self.fps as f64).round().max(0.0) as i64;
        match mapped.blend {
            TimelineBlend::Outgoing => {
                if timestamp > self.last_pts {
                    self.pending_outgoing
                        .entry(timestamp)
                        .or_insert_with(|| self.converted.clone());
                }
                return Ok(());
            }
            TimelineBlend::Incoming { progress } => {
                if let Some(outgoing) = self.take_pending_outgoing(timestamp) {
                    blend_video_frames(&outgoing, &mut self.converted, progress);
                }
            }
            TimelineBlend::Normal => {}
        }
        self.send_converted(timestamp)
    }

    fn send_converted(&mut self, timestamp: i64) -> Result<(), FinalizeError> {
        if timestamp <= self.last_pts {
            return Ok(());
        }
        self.last_pts = timestamp;
        self.converted.set_pts(Some(timestamp));
        self.encoder
            .send_frame(&self.converted)
            .map_err(FinalizeError::SendFrame)?;
        self.write_available_packets()
    }

    fn take_pending_outgoing(&mut self, timestamp: i64) -> Option<frame::Video> {
        let start = timestamp.saturating_sub(1);
        let end = timestamp.saturating_add(1);
        let key = self
            .pending_outgoing
            .range(start..=end)
            .min_by_key(|(candidate, _)| candidate.abs_diff(timestamp))
            .map(|(candidate, _)| *candidate)?;
        self.pending_outgoing.remove(&key)
    }

    fn flush_pending_outgoing(&mut self) -> Result<(), FinalizeError> {
        let pending = std::mem::take(&mut self.pending_outgoing);
        for (timestamp, frame) in pending {
            self.converted.clone_from(&frame);
            self.send_converted(timestamp)?;
        }
        Ok(())
    }

    fn finish(&mut self) -> Result<(), FinalizeError> {
        if self.finished {
            return Ok(());
        }
        self.flush_pending_outgoing()?;
        self.encoder
            .send_eof()
            .map_err(FinalizeError::FlushEncoder)?;
        self.write_available_packets()?;
        self.output
            .write_trailer()
            .map_err(FinalizeError::Trailer)?;
        self.finished = true;
        Ok(())
    }

    fn write_available_packets(&mut self) -> Result<(), FinalizeError> {
        let mut packet = Packet::empty();
        loop {
            match self.encoder.receive_packet(&mut packet) {
                Ok(()) => {
                    packet.set_stream(self.stream_index);
                    packet.set_position(-1);
                    packet.rescale_ts(self.encoder_time_base, self.stream_time_base);
                    packet
                        .write_interleaved(&mut self.output)
                        .map_err(FinalizeError::Packet)?;
                }
                Err(ffmpeg::Error::Other { errno }) if errno == ffmpeg::error::EAGAIN => break,
                Err(ffmpeg::Error::Eof) => break,
                Err(error) => return Err(FinalizeError::Packet(error)),
            }
        }
        Ok(())
    }
}

fn blend_video_frames(outgoing: &frame::Video, incoming: &mut frame::Video, progress: f64) {
    let incoming_weight = progress.clamp(0.0, 1.0);
    let outgoing_weight = 1.0 - incoming_weight;
    for plane in 0..incoming.planes().min(outgoing.planes()) {
        let source = outgoing.data(plane);
        let destination = incoming.data_mut(plane);
        for (incoming, outgoing) in destination.iter_mut().zip(source) {
            *incoming = (f64::from(*outgoing) * outgoing_weight
                + f64::from(*incoming) * incoming_weight)
                .round()
                .clamp(0.0, 255.0) as u8;
        }
    }
}

impl Drop for TimelineOutput {
    fn drop(&mut self) {
        let _ = self.finish();
    }
}

fn temporary_output_path(output: &Path) -> PathBuf {
    let extension = output
        .extension()
        .and_then(|value| value.to_str())
        .unwrap_or("mkv");
    output.with_extension(format!("working.{extension}"))
}

fn working_path(output: &Path, purpose: &str, extension: &str) -> PathBuf {
    let stem = output
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or("recording");
    output.with_file_name(format!("{stem}.working.{purpose}.{extension}"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::encoder::VideoFileEncoder;
    use crate::{AudioChunk, CaptureConfig, CapturedFrame, write_audio_chunk};
    use std::fs::File;
    use std::io::{BufWriter, Write};
    use std::time::{SystemTime, UNIX_EPOCH};

    #[test]
    fn timeline_selection_concatenates_disjoint_ranges() {
        let clips = vec![
            FinalizeClip {
                source: "room.mkv".to_owned(),
                start_seconds: 1.0,
                duration_seconds: 2.0,
                music_event: String::new(),
                music_timeline_milliseconds: 0,
                seamless_from_previous: false,
                bgm_follows_video: false,
            },
            FinalizeClip {
                source: "room.mkv".to_owned(),
                start_seconds: 5.0,
                duration_seconds: 1.0,
                music_event: String::new(),
                music_timeline_milliseconds: 0,
                seamless_from_previous: false,
                bgm_follows_video: false,
            },
        ];
        let mut selection = TimelineSelection::new(&clips);
        assert_eq!(selection.map(0.5), None);
        assert_eq!(
            selection.map(1.5),
            Some(MappedFrame {
                output_seconds: 0.5,
                blend: TimelineBlend::Normal,
            })
        );
        let outgoing = selection.map(2.8).unwrap();
        assert!((outgoing.output_seconds - 1.8).abs() < 1e-9);
        assert_eq!(outgoing.blend, TimelineBlend::Outgoing);
        assert_eq!(selection.map(4.0), None);
        assert_eq!(
            selection.map(5.125),
            Some(MappedFrame {
                output_seconds: 1.875,
                blend: TimelineBlend::Incoming { progress: 0.5 },
            })
        );
        assert_eq!(
            selection.map(5.25),
            Some(MappedFrame {
                output_seconds: 2.0,
                blend: TimelineBlend::Normal,
            })
        );
        assert_eq!(selection.map(6.0), None);
        assert!(selection.finished());
    }

    #[test]
    fn adjacent_metadata_clips_do_not_create_a_transition() {
        let clips = vec![
            FinalizeClip {
                source: "room.mkv".to_owned(),
                start_seconds: 0.0,
                duration_seconds: 1.0,
                music_event: "event:/music/a".to_owned(),
                music_timeline_milliseconds: 0,
                seamless_from_previous: false,
                bgm_follows_video: false,
            },
            FinalizeClip {
                source: "room.mkv".to_owned(),
                start_seconds: 1.0,
                duration_seconds: 1.0,
                music_event: "event:/music/a".to_owned(),
                music_timeline_milliseconds: 1_000,
                seamless_from_previous: false,
                bgm_follows_video: false,
            },
        ];
        let layout = timeline_layout(&clips);
        assert_eq!(layout[0].fade_out_seconds, 0.0);
        assert_eq!(layout[1].fade_in_seconds, 0.0);
        assert_eq!(layout[1].output_start_seconds, 1.0);
    }

    #[test]
    fn removing_freeze_segments_splits_clips_and_keeps_music_continuous() {
        let clips = vec![FinalizeClip {
            source: "room.mkv".to_owned(),
            start_seconds: 0.0,
            duration_seconds: 3.0,
            music_event: "event:/music/a".to_owned(),
            music_timeline_milliseconds: 100,
            seamless_from_previous: false,
            bgm_follows_video: false,
        }];
        let edited = remove_freeze_segments(
            &clips,
            &[FreezeSegment {
                start_seconds: 1.0,
                end_seconds: 2.0,
            }],
        );
        assert_eq!(edited.len(), 2);
        assert!((edited[0].duration_seconds - 1.0).abs() < 1e-9);
        assert!((edited[1].start_seconds - 2.0).abs() < 1e-9);
        assert!((edited[1].duration_seconds - 1.0).abs() < 1e-9);
        assert_eq!(edited[0].music_timeline_milliseconds, 100);
        assert_eq!(edited[1].music_timeline_milliseconds, 1_100);
        assert!(!edited[0].seamless_from_previous);
        assert!(edited[1].seamless_from_previous);
    }

    #[test]
    fn room_music_policy_survives_json_and_freeze_splits() {
        let clip: FinalizeClip = serde_json::from_str(
            r#"{
            "source":"run.mkv","start_seconds":0,"duration_seconds":3,
            "music_timeline_milliseconds":100,"bgm_follows_video":true
        }"#,
        )
        .unwrap();
        let edited = remove_freeze_segments(
            &[clip],
            &[FreezeSegment {
                start_seconds: 1.0,
                end_seconds: 2.0,
            }],
        );
        assert_eq!(edited.len(), 2);
        assert!(edited.iter().all(|clip| clip.bgm_follows_video));
        assert_eq!(edited[1].music_timeline_milliseconds, 2100);
        assert!(edited[1].seamless_from_previous);
        let legacy: FinalizeClip = serde_json::from_str(
            r#"{
            "source":"run.mkv","start_seconds":0,"duration_seconds":1
        }"#,
        )
        .unwrap();
        assert!(!legacy.bgm_follows_video);
    }

    #[test]
    fn seamless_clip_uses_a_frame_exact_cut_without_fading() {
        let clips = vec![
            FinalizeClip {
                source: "room.mkv".to_owned(),
                start_seconds: 0.0,
                duration_seconds: 1.0,
                music_event: String::new(),
                music_timeline_milliseconds: 0,
                seamless_from_previous: false,
                bgm_follows_video: false,
            },
            FinalizeClip {
                source: "room.mkv".to_owned(),
                start_seconds: 4.0,
                duration_seconds: 1.0,
                music_event: String::new(),
                music_timeline_milliseconds: 0,
                seamless_from_previous: true,
                bgm_follows_video: false,
            },
        ];
        let layout = timeline_layout(&clips);
        assert_eq!(layout[0].fade_out_seconds, 0.0);
        assert_eq!(layout[1].fade_in_seconds, 0.0);
        assert_eq!(layout[1].output_start_seconds, 1.0);

        let mut selection = TimelineSelection::new(&clips);
        assert_eq!(selection.map(0.9).unwrap().blend, TimelineBlend::Normal);
        assert_eq!(selection.map(4.0).unwrap().blend, TimelineBlend::Normal);
    }

    #[test]
    fn video_frame_blending_interpolates_the_transition() {
        let mut outgoing = frame::Video::new(ffmpeg::format::Pixel::YUV420P, 2, 2);
        let mut incoming = frame::Video::new(ffmpeg::format::Pixel::YUV420P, 2, 2);
        for plane in 0..outgoing.planes() {
            outgoing.data_mut(plane).fill(20);
            incoming.data_mut(plane).fill(220);
        }
        blend_video_frames(&outgoing, &mut incoming, 0.5);
        for plane in 0..incoming.planes() {
            assert!(incoming.data(plane).iter().all(|value| *value == 120));
        }
    }

    #[test]
    fn transcodes_arbitrary_ranges_from_one_continuous_source_when_enabled() {
        if std::env::var_os("MQOL_TEST_FFMPEG").is_none() {
            return;
        }
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("continuous.mkv");
        write_continuous_source(&source);
        write_continuous_audio(&source);
        let output = directory.path().join("joined.mp4");
        finalize(&FinalizePlan {
            clips: vec![
                FinalizeClip {
                    source: source.to_string_lossy().into_owned(),
                    start_seconds: 0.25,
                    duration_seconds: 0.5,
                    music_event: String::new(),
                    music_timeline_milliseconds: 0,
                    seamless_from_previous: false,
                    bgm_follows_video: false,
                },
                FinalizeClip {
                    source: source.to_string_lossy().into_owned(),
                    start_seconds: 1.5,
                    duration_seconds: 0.5,
                    music_event: String::new(),
                    music_timeline_milliseconds: 0,
                    seamless_from_previous: false,
                    bgm_follows_video: false,
                },
            ],
            output_path: output.to_string_lossy().into_owned(),
            encoder: "libopenh264".to_owned(),
            // The discontinuous replay must fall back to the existing exact edit.
            prefer_video_copy: true,
            bitrate_kbps: 1_000,
            fps: 30,
            ..FinalizePlan::default()
        })
        .unwrap();
        assert!(fs::metadata(&output).unwrap().len() > 1_000);
        let mut joined = format::input(&output).unwrap();
        let video_stream = joined.streams().best(media::Type::Video).unwrap();
        let stream_index = video_stream.index();
        let time_base = video_stream.time_base();
        let video_stream_duration = video_stream.duration() as f64 * f64::from(time_base);
        drop(video_stream);
        let audio_stream = joined.streams().best(media::Type::Audio).unwrap();
        assert_eq!(audio_stream.parameters().id(), codec::Id::AAC);
        let audio_duration = audio_stream.duration() as f64 * f64::from(audio_stream.time_base());
        drop(audio_stream);
        let mut last_timestamp = 0_i64;
        for (stream, packet) in joined.packets() {
            if stream.index() == stream_index {
                last_timestamp = last_timestamp.max(packet.pts().unwrap_or(0));
            }
        }
        let video_duration = last_timestamp as f64 * f64::from(time_base);
        assert!(
            (0.60..0.75).contains(&video_duration),
            "unexpected crossfaded video duration {video_duration}"
        );
        assert!((0.60..0.75).contains(&video_stream_duration));
        assert!((0.70..0.82).contains(&audio_duration));
        assert!((audio_duration - video_stream_duration).abs() < 0.15);
    }

    fn write_continuous_source(path: &Path) {
        let config = CaptureConfig {
            output_path: Some(path.to_string_lossy().into_owned()),
            encoder: "libopenh264".to_owned(),
            fps: 30,
            bitrate_kbps: 1_000,
            ..CaptureConfig::default()
        };
        let start = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos() as u64;
        let mut frame = CapturedFrame {
            format: crate::CapturePixelFormat::Bgra,
            width: 64,
            height: 64,
            captured_at_unix_nanos: start,
            pixels: vec![0; 64 * 64 * 4],
        };
        let mut encoder = VideoFileEncoder::create(&config, &frame).unwrap();
        for index in 0..90_u64 {
            frame.captured_at_unix_nanos = start + index * 1_000_000_000 / 30;
            for pixel in frame.pixels.chunks_exact_mut(4) {
                pixel[0] = (index * 3) as u8;
                pixel[1] = (index * 5) as u8;
                pixel[2] = (index * 7) as u8;
                pixel[3] = 255;
            }
            encoder.encode(&frame).unwrap();
        }
        encoder.finish().unwrap();
    }

    #[test]
    fn death_replay_copies_non_keyframe_trim_and_preserves_encoded_packets() {
        if std::env::var_os("MQOL_TEST_FFMPEG").is_none() {
            return;
        }
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("continuous.mkv");
        write_continuous_source(&source);
        write_continuous_audio(&source);
        let reference = decoded_samples(&source);
        let reference_packets = encoded_samples(&source);
        // Both within a GOP and after a seek to the next GOP. Also cover a cut
        // at zero, a sub-frame replay, metadata splits and the no-audio path.
        for (index, start, duration, audio) in [
            (0, 0.25, 0.5, true),
            (1, 2.25, 0.5, true),
            (2, 0.0, 0.5, true),
            (3, 0.26, 0.02, true),
            (5, 1.99, 0.5, true),
            (4, 0.25, 0.5, false),
        ] {
            if !audio {
                fs::remove_file(format!("{}.sfxchunks", source.display())).unwrap();
            }
            let output = directory.path().join(format!("copied-{index}.mp4"));
            let clip = FinalizeClip {
                source: source.to_string_lossy().into_owned(),
                start_seconds: start,
                duration_seconds: duration / 2.,
                music_event: String::new(),
                music_timeline_milliseconds: 0,
                seamless_from_previous: false,
                bgm_follows_video: false,
            };
            let mut second = clip.clone();
            second.start_seconds += duration / 2.;
            second.seamless_from_previous = true;
            second.bgm_follows_video = true;
            let mut progress = Vec::new();
            finalize_with_progress(
                &FinalizePlan {
                    clips: vec![clip, second],
                    output_path: output.to_string_lossy().into_owned(),
                    // Packet identity below proves reuse even though the encoder
                    // selector could fall back from an unavailable preference.
                    encoder: "mqol_test_no_such_encoder".into(),
                    prefer_video_copy: true,
                    reconstruct_bgm: true,
                    fps: 30,
                    ..FinalizePlan::default()
                },
                |p| progress.push(p),
            )
            .unwrap();
            assert_eq!(progress.last(), Some(&1.));
            assert!(progress.windows(2).all(|p| p[0] <= p[1]));
            let actual = decoded_samples(&output);
            let actual_packets: Vec<_> = encoded_samples(&output)
                .into_iter()
                .filter(|(t, _)| *t >= 0.)
                .map(|(_, bytes)| bytes)
                .collect();
            let expected_packets: Vec<_> = reference_packets
                .iter()
                .filter(|(t, _)| *t >= start && *t < start + duration)
                .map(|(_, bytes)| bytes.clone())
                .collect();
            assert_eq!(
                actual_packets, expected_packets,
                "compressed packets changed, case {index}"
            );
            let expected: Vec<_> = reference
                .iter()
                .filter(|(t, _)| *t >= start && *t < start + duration)
                .collect();
            assert_eq!(
                actual.len(),
                expected.len(),
                "visible frame count case {index}: {actual:?}"
            );
            for ((actual_time, actual_pixel), (source_time, expected_pixel)) in
                actual.iter().zip(expected)
            {
                assert_eq!(
                    actual_pixel, expected_pixel,
                    "preroll leaked or video was re-encoded, case {index}"
                );
                // MP4's edit list rounds its initial visible sample to zero;
                // source time remains within one capture frame of the audio.
                assert!(
                    (actual_time - (source_time - start)).abs() < 1. / 30. + 0.001,
                    "wrong trim timestamp case {index}: {actual_time} vs {source_time}"
                );
            }
            let input = format::input(&output).unwrap();
            let video = input.streams().best(media::Type::Video).unwrap();
            let video_duration = video.duration() as f64 * f64::from(video.time_base());
            assert!(
                (video_duration - duration).abs() < 0.04,
                "video duration {video_duration}"
            );
            let audio_stream = input.streams().best(media::Type::Audio);
            assert_eq!(audio_stream.is_some(), audio);
            if let Some(audio) = audio_stream {
                let audio_duration = audio.duration() as f64 * f64::from(audio.time_base());
                assert!(audio.start_time().abs() as f64 * f64::from(audio.time_base()) < 0.002);
                assert!(
                    (audio_duration - duration).abs() < 0.04,
                    "audio duration {audio_duration}"
                );
            }
        }
    }

    fn decoded_samples(path: &Path) -> Vec<(f64, [u8; 3])> {
        let mut input = format::input(path).unwrap();
        let video = input.streams().best(media::Type::Video).unwrap();
        let index = video.index();
        let time_base = f64::from(video.time_base());
        let mut decoder = codec::context::Context::from_parameters(video.parameters())
            .unwrap()
            .decoder()
            .video()
            .unwrap();
        drop(video);
        let mut result = Vec::new();
        let mut decoded = frame::Video::empty();
        let mut drain = |decoder: &mut ffmpeg::decoder::Video| {
            while decoder.receive_frame(&mut decoded).is_ok() {
                result.push((
                    decoded.timestamp().unwrap() as f64 * time_base,
                    [decoded.data(0)[0], decoded.data(1)[0], decoded.data(2)[0]],
                ));
            }
        };
        for (stream, packet) in input.packets() {
            if stream.index() != index {
                continue;
            }
            decoder.send_packet(&packet).unwrap();
            drain(&mut decoder);
        }
        decoder.send_eof().unwrap();
        drain(&mut decoder);
        result
    }

    fn encoded_samples(path: &Path) -> Vec<(f64, Vec<u8>)> {
        let mut input = format::input(path).unwrap();
        let video = input.streams().best(media::Type::Video).unwrap();
        let index = video.index();
        let time_base = f64::from(video.time_base());
        drop(video);
        input
            .packets()
            .filter(|(stream, _)| stream.index() == index)
            .map(|(_, packet)| {
                (
                    packet.pts().unwrap() as f64 * time_base,
                    packet.data().unwrap().to_vec(),
                )
            })
            .collect()
    }

    fn write_continuous_audio(video_path: &Path) {
        let path = PathBuf::from(format!("{}.sfxchunks", video_path.display()));
        let mut writer = BufWriter::new(File::create(path).unwrap());
        writer.write_all(b"MQOLAUD1").unwrap();
        let sample_rate = 48_000_u32;
        let channels = 2_u16;
        let frames_per_chunk = 1_024_usize;
        for chunk_index in 0..141_u64 {
            let media_time_nanos =
                chunk_index * frames_per_chunk as u64 * 1_000_000_000 / u64::from(sample_rate);
            for (bus_id, frequency, amplitude) in [(1_u16, 440.0_f32, 0.20_f32), (2, 660.0, 0.10)] {
                let mut samples = Vec::with_capacity(frames_per_chunk * usize::from(channels));
                for local_frame in 0..frames_per_chunk {
                    let absolute_frame = chunk_index as usize * frames_per_chunk + local_frame;
                    let value = (absolute_frame as f32 * frequency * std::f32::consts::TAU
                        / sample_rate as f32)
                        .sin()
                        * amplitude;
                    samples.extend_from_slice(&[value, value]);
                }
                write_audio_chunk(
                    &mut writer,
                    &AudioChunk {
                        media_time_nanos,
                        sample_rate,
                        channels,
                        bus_id,
                        samples,
                    },
                )
                .unwrap();
            }
        }
        writer.flush().unwrap();
    }
}
