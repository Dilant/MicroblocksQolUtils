use std::collections::HashMap;
use std::fs::{self, File, OpenOptions};
use std::io::{BufRead, BufReader, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};

use ffmpeg::{ChannelLayout, Packet, Rational, codec, encoder, format, frame, media, software};
use ffmpeg_next as ffmpeg;
use thiserror::Error;

use crate::finalizer::{CUT_GAP_SECONDS, FinalizeClip, TimelineClipLayout, timeline_layout};

const SIDECAR_MAGIC: &[u8; 8] = b"MQOLAUD1";
const CHUNK_HEADER_BYTES: usize = 24;
const MAX_CHUNK_SAMPLES: usize = 16_384;
const AUDIO_BITRATE: usize = 192_000;
const DEFAULT_SAMPLE_RATE: u32 = 48_000;
const DEFAULT_CHANNELS: u16 = 2;

#[derive(Debug, Error)]
pub enum AudioFinalizeError {
    #[error("cannot access audio sidecar {path}: {source}")]
    Io {
        path: PathBuf,
        source: std::io::Error,
    },
    #[error("invalid audio sidecar {path}: {detail}")]
    InvalidSidecar { path: PathBuf, detail: String },
    #[error("unsupported captured audio layout: {channels} channels")]
    Channels { channels: u16 },
    #[error("audio timeline is too large")]
    TimelineTooLarge,
    #[error("cannot read BGM event map {path}: {source}")]
    ReadBgmMap {
        path: PathBuf,
        source: std::io::Error,
    },
    #[error("invalid BGM event map {path}: {source}")]
    ParseBgmMap {
        path: PathBuf,
        source: serde_json::Error,
    },
    #[error("cannot open mapped BGM {path}: {source}")]
    OpenBgm {
        path: PathBuf,
        source: ffmpeg::Error,
    },
    #[error("mapped BGM has no audio stream: {0}")]
    MissingBgmAudio(PathBuf),
    #[error("cannot decode mapped BGM: {0}")]
    DecodeBgm(ffmpeg::Error),
    #[error("cannot resample mapped BGM: {0}")]
    ResampleBgm(ffmpeg::Error),
    #[error("FFmpeg AAC encoder is unavailable")]
    MissingAac,
    #[error("AAC encoder does not support planar f32 samples")]
    UnsupportedAacFormat,
    #[error("cannot create audio output {path}: {source}")]
    CreateAudioOutput {
        path: PathBuf,
        source: ffmpeg::Error,
    },
    #[error("cannot configure AAC output: {0}")]
    ConfigureAac(ffmpeg::Error),
    #[error("AAC encoder rejected an audio frame: {0}")]
    SendAudio(ffmpeg::Error),
    #[error("cannot write AAC packet: {0}")]
    AudioPacket(ffmpeg::Error),
    #[error("cannot write AAC trailer: {0}")]
    AudioTrailer(ffmpeg::Error),
    #[error("cannot open mux input {path}: {source}")]
    OpenMuxInput {
        path: PathBuf,
        source: ffmpeg::Error,
    },
    #[error("mux input {path} has no {kind} stream")]
    MissingMuxStream { path: PathBuf, kind: &'static str },
    #[error("cannot create mux output {path}: {source}")]
    CreateMuxOutput {
        path: PathBuf,
        source: ffmpeg::Error,
    },
    #[error("cannot configure mux output: {0}")]
    ConfigureMux(ffmpeg::Error),
    #[error("cannot write muxed packet: {0}")]
    MuxPacket(ffmpeg::Error),
    #[error("cannot write mux trailer: {0}")]
    MuxTrailer(ffmpeg::Error),
}

#[derive(Debug, Clone, Copy)]
struct AudioSpec {
    sample_rate: u32,
    channels: u16,
    total_frames: u64,
}

#[derive(Debug, Clone, Copy)]
struct AudioClipLayout {
    output_start_frames: u64,
    clip_frames: u64,
    fade_in_frames: u64,
    fade_out_frames: u64,
}

#[derive(Debug, Clone)]
struct PostMixSegment {
    output_start_frames: u64,
    frames: u64,
    captured_source_start_frames: u64,
    mapped_source_start_seconds: f64,
    music_event: String,
    bgm_follows_video: bool,
}

impl AudioClipLayout {
    fn gain_at(self, local_frame: u64) -> f32 {
        let fade_in = if self.fade_in_frames > 0 && local_frame < self.fade_in_frames {
            local_frame as f32 / self.fade_in_frames as f32
        } else {
            1.0
        };
        let fade_out_start = self.clip_frames.saturating_sub(self.fade_out_frames);
        let fade_out = if self.fade_out_frames > 0 && local_frame >= fade_out_start {
            (self.clip_frames.saturating_sub(local_frame)) as f32 / self.fade_out_frames as f32
        } else {
            1.0
        };
        fade_in.min(fade_out).clamp(0.0, 1.0)
    }
}

#[derive(Debug)]
struct SidecarChunk {
    media_time_nanos: u64,
    sample_rate: u32,
    channels: u16,
    bus_id: u16,
    samples: Vec<f32>,
}

pub fn build_audio_track(
    sidecar: &Path,
    clips: &[FinalizeClip],
    mixed_pcm: &Path,
    audio_output: &Path,
    reconstruct_bgm: bool,
    bgm_event_map_file: Option<&Path>,
) -> Result<bool, AudioFinalizeError> {
    let bgm_map = bgm_event_map_file
        .map(load_bgm_map)
        .transpose()?
        .unwrap_or_default();
    // Names may come from the independent journal rather than clip-start metadata.
    let has_mapped_bgm = !bgm_map.is_empty();
    let separate_bgm = sidecar.with_extension("bgmchunks");
    let bgm_sidecar = if separate_bgm.exists() {
        &separate_bgm
    } else {
        sidecar
    };
    let captured_spec = sidecar
        .exists()
        .then(|| render_mix(sidecar, clips, mixed_pcm, reconstruct_bgm))
        .transpose()?
        .flatten();
    if captured_spec.is_none() && !has_mapped_bgm && !separate_bgm.exists() {
        return Ok(false);
    }
    let spec = captured_spec.unwrap_or(AudioSpec {
        sample_rate: DEFAULT_SAMPLE_RATE,
        channels: DEFAULT_CHANNELS,
        total_frames: total_timeline_frames(clips, DEFAULT_SAMPLE_RATE)?,
    });
    if captured_spec.is_none() {
        create_empty_mix(mixed_pcm, spec)?;
    }
    if reconstruct_bgm {
        if separate_bgm.exists() {
            mix_rendered_bgm(&separate_bgm, mixed_pcm, spec)?;
        } else if bgm_sidecar.exists() {
            mix_captured_bgm(bgm_sidecar, mixed_pcm, clips, &bgm_map, spec)?;
        }
        if has_mapped_bgm {
            mix_bgm_tracks(bgm_sidecar, mixed_pcm, clips, &bgm_map, spec)?;
        }
    } else if separate_bgm.exists() {
        // Normal export still cuts music with video, but never mixes it into the SFX source.
        mix_rendered_bgm(&separate_bgm, mixed_pcm, spec)?;
    }
    encode_aac(mixed_pcm, audio_output, spec)?;
    Ok(true)
}

fn mix_rendered_bgm(
    sidecar: &Path,
    mixed_pcm: &Path,
    spec: AudioSpec,
) -> Result<(), AudioFinalizeError> {
    let clip = FinalizeClip {
        source: String::new(),
        start_seconds: 0.0,
        duration_seconds: spec.total_frames as f64 / f64::from(spec.sample_rate),
        music_event: String::new(),
        music_timeline_milliseconds: 0,
        seamless_from_previous: false,
        bgm_follows_video: false,
    };
    mix_separate_bgm_with_video(sidecar, mixed_pcm, &[clip], spec)
}

fn render_mix(
    sidecar: &Path,
    clips: &[FinalizeClip],
    mixed_pcm: &Path,
    exclude_captured_bgm: bool,
) -> Result<Option<AudioSpec>, AudioFinalizeError> {
    let file = File::open(sidecar).map_err(|source| io_error(sidecar, source))?;
    let mut reader = BufReader::new(file);
    let mut magic = [0_u8; 8];
    reader
        .read_exact(&mut magic)
        .map_err(|source| io_error(sidecar, source))?;
    if &magic != SIDECAR_MAGIC {
        return Err(invalid(sidecar, "bad magic"));
    }
    let Some(first) = read_chunk(&mut reader, sidecar)? else {
        return Ok(None);
    };
    if !(1..=2).contains(&first.channels) {
        return Err(AudioFinalizeError::Channels {
            channels: first.channels,
        });
    }
    let clip_layout = audio_timeline_layout(clips, first.sample_rate)?;
    let total_frames = total_frames_from_layout(&clip_layout)?;
    let total_bytes = total_frames
        .checked_mul(u64::from(first.channels))
        .and_then(|samples| samples.checked_mul(4))
        .ok_or(AudioFinalizeError::TimelineTooLarge)?;
    let mut mixed = OpenOptions::new()
        .create(true)
        .truncate(true)
        .read(true)
        .write(true)
        .open(mixed_pcm)
        .map_err(|source| io_error(mixed_pcm, source))?;
    mixed
        .set_len(total_bytes)
        .map_err(|source| io_error(mixed_pcm, source))?;

    mix_chunk(
        &mut mixed,
        &first,
        clips,
        &clip_layout,
        first.sample_rate,
        first.channels,
        exclude_captured_bgm,
        sidecar,
    )?;
    let mut contains_bgm = first.bus_id == 3;
    while let Some(chunk) = read_chunk(&mut reader, sidecar)? {
        contains_bgm |= chunk.bus_id == 3;
        if chunk.sample_rate != first.sample_rate || chunk.channels != first.channels {
            return Err(invalid(
                sidecar,
                format!(
                    "audio format changed from {} Hz/{} ch to {} Hz/{} ch",
                    first.sample_rate, first.channels, chunk.sample_rate, chunk.channels
                ),
            ));
        }
        mix_chunk(
            &mut mixed,
            &chunk,
            clips,
            &clip_layout,
            first.sample_rate,
            first.channels,
            exclude_captured_bgm,
            sidecar,
        )?;
    }
    // SFX-only de-click before adding the separate continuous music track.
    // Never borrow samples from the excluded attempt/death range.
    if exclude_captured_bgm || !contains_bgm {
        smooth_sfx_seams(
            &mut mixed,
            clips,
            &clip_layout,
            first.sample_rate,
            first.channels,
        )?;
    }
    mixed
        .flush()
        .map_err(|source| io_error(mixed_pcm, source))?;
    Ok(Some(AudioSpec {
        sample_rate: first.sample_rate,
        channels: first.channels,
        total_frames,
    }))
}

fn mix_separate_bgm_with_video(
    sidecar: &Path,
    mixed_pcm: &Path,
    clips: &[FinalizeClip],
    spec: AudioSpec,
) -> Result<(), AudioFinalizeError> {
    let mut reader = BufReader::new(File::open(sidecar).map_err(|e| io_error(sidecar, e))?);
    let mut magic = [0u8; 8];
    reader
        .read_exact(&mut magic)
        .map_err(|e| io_error(sidecar, e))?;
    if &magic != SIDECAR_MAGIC {
        return Err(invalid(sidecar, "bad magic"));
    }
    let layout = audio_timeline_layout(clips, spec.sample_rate)?;
    let mut mixed = OpenOptions::new()
        .read(true)
        .write(true)
        .open(mixed_pcm)
        .map_err(|e| io_error(mixed_pcm, e))?;
    while let Some(chunk) = read_chunk(&mut reader, sidecar)? {
        if chunk.sample_rate != spec.sample_rate {
            return Err(invalid(sidecar, "FMOD sample rate changed"));
        }
        if chunk.bus_id != 3 {
            return Err(invalid(sidecar, "non-music data in BGM sidecar"));
        }
        mix_chunk(
            &mut mixed,
            &chunk,
            clips,
            &layout,
            spec.sample_rate,
            spec.channels,
            false,
            sidecar,
        )?;
    }
    mixed.flush().map_err(|e| io_error(mixed_pcm, e))
}

fn total_timeline_frames(
    clips: &[FinalizeClip],
    sample_rate: u32,
) -> Result<u64, AudioFinalizeError> {
    total_frames_from_layout(&audio_timeline_layout(clips, sample_rate)?)
}

fn audio_timeline_layout(
    clips: &[FinalizeClip],
    sample_rate: u32,
) -> Result<Vec<AudioClipLayout>, AudioFinalizeError> {
    timeline_layout(clips)
        .into_iter()
        .zip(clips)
        .map(|(layout, clip)| audio_clip_layout(layout, clip, sample_rate))
        .collect()
}

fn audio_clip_layout(
    layout: TimelineClipLayout,
    clip: &FinalizeClip,
    sample_rate: u32,
) -> Result<AudioClipLayout, AudioFinalizeError> {
    Ok(AudioClipLayout {
        output_start_frames: seconds_to_frames(layout.output_start_seconds, sample_rate)?,
        clip_frames: seconds_to_frames(clip.duration_seconds, sample_rate)?,
        fade_in_frames: seconds_to_frames(layout.fade_in_seconds, sample_rate)?,
        fade_out_frames: seconds_to_frames(layout.fade_out_seconds, sample_rate)?,
    })
}

fn total_frames_from_layout(layout: &[AudioClipLayout]) -> Result<u64, AudioFinalizeError> {
    layout.iter().try_fold(0_u64, |total, clip| {
        Ok(total.max(
            clip.output_start_frames
                .checked_add(clip.clip_frames)
                .ok_or(AudioFinalizeError::TimelineTooLarge)?,
        ))
    })
}

fn create_empty_mix(path: &Path, spec: AudioSpec) -> Result<(), AudioFinalizeError> {
    let total_bytes = spec
        .total_frames
        .checked_mul(u64::from(spec.channels))
        .and_then(|samples| samples.checked_mul(4))
        .ok_or(AudioFinalizeError::TimelineTooLarge)?;
    let file = OpenOptions::new()
        .create(true)
        .truncate(true)
        .read(true)
        .write(true)
        .open(path)
        .map_err(|source| io_error(path, source))?;
    file.set_len(total_bytes)
        .map_err(|source| io_error(path, source))
}

fn load_bgm_map(path: &Path) -> Result<HashMap<String, PathBuf>, AudioFinalizeError> {
    let bytes = fs::read(path).map_err(|source| AudioFinalizeError::ReadBgmMap {
        path: path.to_owned(),
        source,
    })?;
    let configured: HashMap<String, String> =
        serde_json::from_slice(&bytes).map_err(|source| AudioFinalizeError::ParseBgmMap {
            path: path.to_owned(),
            source,
        })?;
    let base = path.parent().unwrap_or_else(|| Path::new("."));
    Ok(configured
        .into_iter()
        .filter_map(|(event, configured_path)| {
            let configured_path = configured_path.trim();
            if event.trim().is_empty() || configured_path.is_empty() {
                return None;
            }
            let audio = PathBuf::from(configured_path);
            Some((
                event,
                if audio.is_absolute() {
                    audio
                } else {
                    base.join(audio)
                },
            ))
        })
        .collect())
}

fn post_mix_segments(
    clips: &[FinalizeClip],
    sample_rate: u32,
) -> Result<Vec<PostMixSegment>, AudioFinalizeError> {
    let layout = audio_timeline_layout(clips, sample_rate)?;
    let total_frames = total_frames_from_layout(&layout)?;
    let mut result: Vec<PostMixSegment> = Vec::with_capacity(clips.len());
    for (index, (clip, clip_layout)) in clips.iter().zip(&layout).enumerate() {
        let output_end = layout
            .get(index + 1)
            .map(|next| next.output_start_frames)
            .unwrap_or(total_frames);
        let frames = output_end.saturating_sub(clip_layout.output_start_frames);
        if frames == 0 {
            continue;
        }
        let actual_captured_start = seconds_to_frames(clip.start_seconds, sample_rate)?;
        let actual_mapped_start = clip.music_timeline_milliseconds.max(0) as f64 / 1_000.0;
        let continues_across_edit = !clip.bgm_follows_video
            && index > 0
            && !result.is_empty()
            && clips[index - 1].music_event == clip.music_event
            && (clip.start_seconds
                - (clips[index - 1].start_seconds + clips[index - 1].duration_seconds)
                > CUT_GAP_SECONDS
                // A room/metadata split after an edit must not reset an already shifted
                // cursor. A contiguous, real timeline jump still starts a new take.
                || (clip.music_timeline_milliseconds - clips[index - 1].music_timeline_milliseconds
                    - (clips[index - 1].duration_seconds * 1000.0).round() as i64).abs() <= 2);
        let (captured_source_start_frames, mapped_source_start_seconds) = if continues_across_edit {
            let previous = result
                .last()
                .expect("an edited continuation must have a preceding BGM segment");
            let output_delta = clip_layout
                .output_start_frames
                .saturating_sub(previous.output_start_frames);
            let expected_mapped_start =
                previous.mapped_source_start_seconds + output_delta as f64 / f64::from(sample_rate);
            let captured_start_seconds =
                (clip.start_seconds + expected_mapped_start - actual_mapped_start).max(0.0);
            (
                seconds_to_frames(captured_start_seconds, sample_rate)?,
                expected_mapped_start,
            )
        } else {
            (actual_captured_start, actual_mapped_start)
        };
        result.push(PostMixSegment {
            output_start_frames: clip_layout.output_start_frames,
            frames,
            captured_source_start_frames,
            mapped_source_start_seconds,
            music_event: clip.music_event.clone(),
            bgm_follows_video: clip.bgm_follows_video,
        });
    }
    Ok(result)
}

#[derive(Debug, Clone, serde::Deserialize)]
struct MusicEvent {
    time_nanos: u64,
    kind: String,
    track: String,
    event: String,
    timeline_milliseconds: i64,
}

// New recordings use the independent control journal, not clip-start guesses.
// Legacy recordings retain their per-clip metadata fallback.
fn journal_segments(
    sidecar: &Path,
    clips: &[FinalizeClip],
    rate: u32,
) -> Result<Vec<PostMixSegment>, AudioFinalizeError> {
    let path = sidecar.with_extension("music.jsonl");
    if !path.exists() {
        return post_mix_segments(clips, rate);
    }
    let file = File::open(&path).map_err(|e| io_error(&path, e))?;
    let mut events = Vec::<MusicEvent>::new();
    let mut complete = false;
    let mut header = false;
    for line in BufReader::new(file).lines() {
        let line = line.map_err(|e| io_error(&path, e))?;
        let value: serde_json::Value =
            serde_json::from_str(&line).map_err(|e| invalid(&path, e.to_string()))?;
        if complete {
            return Err(invalid(&path, "data after music journal end"));
        }
        match value["type"].as_str() {
            Some("header") if !header && events.is_empty() && value["version"] == 1 => {
                header = true
            }
            Some("event") if header => events
                .push(serde_json::from_value(value).map_err(|e| invalid(&path, e.to_string()))?),
            Some("end") if header && value["complete"] == true => complete = true,
            _ => return Err(invalid(&path, "unsupported or incomplete music journal")),
        }
    }
    if !complete {
        return Err(invalid(&path, "music journal was not completed"));
    }
    // Stable sort retains command order at an identical timestamp.
    events.sort_by_key(|e| e.time_nanos);
    // Natural STARTING -> PLAYING is not a new musical take. Switch/stop/seek,
    // pause and interactive parameter changes ARE boundaries, including alt music.
    events.retain(|e| e.kind != "playback" && !e.kind.starts_with("command"));
    let layout = audio_timeline_layout(clips, rate)?;
    let total = total_frames_from_layout(&layout)?;
    let mut result: Vec<PostMixSegment> = Vec::new();
    let mut previous_run = None;
    for (index, (clip, output)) in clips.iter().zip(&layout).enumerate() {
        let output_end = layout
            .get(index + 1)
            .map(|l| l.output_start_frames)
            .unwrap_or(total);
        let start = seconds_to_frames(clip.start_seconds, rate)?;
        let end = start + output_end.saturating_sub(output.output_start_frames);
        let mut cuts = vec![start];
        cuts.extend(
            events
                .iter()
                .map(|e| nanos_to_frames(e.time_nanos, rate))
                .filter(|&t| t > start && t < end),
        );
        cuts.push(end);
        cuts.sort_unstable();
        cuts.dedup();
        for interval in cuts.windows(2) {
            let actual = interval[0];
            let run = events.partition_point(|e| nanos_to_frames(e.time_nanos, rate) <= actual);
            let output_start = output.output_start_frames + actual - start;
            let main = events[..run].iter().rev().find(|e| e.track == "main");
            let mut source_start = actual;
            let mut mapped_start = main
                .map(|e| {
                    e.timeline_milliseconds.max(0) as f64 / 1000.0
                        + (actual - nanos_to_frames(e.time_nanos, rate)) as f64 / rate as f64
                })
                .unwrap_or(0.0);
            if !clip.bgm_follows_video && previous_run == Some(run) {
                if let Some(previous) = result.last() {
                    let delta = output_start - previous.output_start_frames;
                    source_start = previous.captured_source_start_frames + delta;
                    mapped_start =
                        previous.mapped_source_start_seconds + delta as f64 / rate as f64;
                }
            }
            result.push(PostMixSegment {
                output_start_frames: output_start,
                frames: interval[1] - actual,
                captured_source_start_frames: source_start,
                mapped_source_start_seconds: mapped_start,
                music_event: main.map(|e| e.event.clone()).unwrap_or_default(),
                bgm_follows_video: clip.bgm_follows_video,
            });
            previous_run = Some(run);
        }
    }
    Ok(result)
}

fn mix_captured_bgm(
    sidecar: &Path,
    mixed_pcm: &Path,
    clips: &[FinalizeClip],
    event_map: &HashMap<String, PathBuf>,
    spec: AudioSpec,
) -> Result<(), AudioFinalizeError> {
    let segments = journal_segments(sidecar, clips, spec.sample_rate)?;
    let file = File::open(sidecar).map_err(|source| io_error(sidecar, source))?;
    let mut reader = BufReader::new(file);
    let mut magic = [0_u8; 8];
    reader
        .read_exact(&mut magic)
        .map_err(|source| io_error(sidecar, source))?;
    if &magic != SIDECAR_MAGIC {
        return Err(invalid(sidecar, "bad magic"));
    }
    let mut mixed = OpenOptions::new()
        .read(true)
        .write(true)
        .open(mixed_pcm)
        .map_err(|source| io_error(mixed_pcm, source))?;
    while let Some(chunk) = read_chunk(&mut reader, sidecar)? {
        if chunk.sample_rate != spec.sample_rate || chunk.channels != spec.channels {
            return Err(invalid(
                sidecar,
                format!(
                    "audio format changed from {} Hz/{} ch to {} Hz/{} ch",
                    spec.sample_rate, spec.channels, chunk.sample_rate, chunk.channels
                ),
            ));
        }
        if chunk.bus_id == 3 {
            mix_captured_bgm_chunk(&mut mixed, &chunk, &segments, event_map, spec)?;
        }
    }
    mixed.flush().map_err(|source| io_error(mixed_pcm, source))
}

fn mix_captured_bgm_chunk(
    mixed: &mut File,
    chunk: &SidecarChunk,
    segments: &[PostMixSegment],
    event_map: &HashMap<String, PathBuf>,
    spec: AudioSpec,
) -> Result<(), AudioFinalizeError> {
    let channels = usize::from(spec.channels);
    let chunk_frames = chunk.samples.len() / channels;
    let chunk_start = nanos_to_frames(chunk.media_time_nanos, spec.sample_rate);
    let chunk_end = chunk_start.saturating_add(chunk_frames as u64);
    for segment in segments {
        if !segment.bgm_follows_video && event_map.contains_key(&segment.music_event) {
            continue;
        }
        let source_end = segment
            .captured_source_start_frames
            .checked_add(segment.frames)
            .ok_or(AudioFinalizeError::TimelineTooLarge)?;
        let overlap_start = chunk_start.max(segment.captured_source_start_frames);
        let overlap_end = chunk_end.min(source_end);
        if overlap_start >= overlap_end {
            continue;
        }
        let source_frame = (overlap_start - chunk_start) as usize;
        let local_frame = overlap_start - segment.captured_source_start_frames;
        let frames = (overlap_end - overlap_start) as usize;
        add_samples(
            mixed,
            segment.output_start_frames + local_frame,
            &chunk.samples[source_frame * channels..(source_frame + frames) * channels],
            spec.channels,
            AudioClipLayout {
                output_start_frames: segment.output_start_frames,
                clip_frames: segment.frames,
                fade_in_frames: 0,
                fade_out_frames: 0,
            },
            local_frame,
        )?;
    }
    Ok(())
}

fn mix_bgm_tracks(
    sidecar: &Path,
    mixed_pcm: &Path,
    clips: &[FinalizeClip],
    event_map: &HashMap<String, PathBuf>,
    spec: AudioSpec,
) -> Result<(), AudioFinalizeError> {
    let mut mixed = OpenOptions::new()
        .read(true)
        .write(true)
        .open(mixed_pcm)
        .map_err(|source| io_error(mixed_pcm, source))?;
    let segments = journal_segments(sidecar, clips, spec.sample_rate)?;
    for segment in segments {
        // Static replacement tracks cannot reproduce this room's live FMOD phase/parameters.
        if segment.bgm_follows_video {
            continue;
        }
        if let Some(path) = event_map.get(&segment.music_event) {
            mix_bgm_segment(
                &mut mixed,
                path,
                segment.mapped_source_start_seconds,
                segment.frames as f64 / f64::from(spec.sample_rate),
                AudioClipLayout {
                    output_start_frames: segment.output_start_frames,
                    clip_frames: segment.frames,
                    fade_in_frames: 0,
                    fade_out_frames: 0,
                },
                spec,
            )?;
        }
    }
    mixed.flush().map_err(|source| io_error(mixed_pcm, source))
}

fn mix_bgm_segment(
    mixed: &mut File,
    path: &Path,
    source_start_seconds: f64,
    duration_seconds: f64,
    clip_layout: AudioClipLayout,
    spec: AudioSpec,
) -> Result<(), AudioFinalizeError> {
    let mut input = format::input(path).map_err(|source| AudioFinalizeError::OpenBgm {
        path: path.to_owned(),
        source,
    })?;
    let stream = input
        .streams()
        .best(media::Type::Audio)
        .ok_or_else(|| AudioFinalizeError::MissingBgmAudio(path.to_owned()))?;
    let stream_index = stream.index();
    let input_time_base = stream.time_base();
    let mut decoder = codec::context::Context::from_parameters(stream.parameters())
        .and_then(|context| context.decoder().audio())
        .map_err(AudioFinalizeError::DecodeBgm)?;
    drop(stream);

    let input_layout = if decoder.channel_layout().is_empty() {
        ChannelLayout::default(i32::from(decoder.channels()))
    } else {
        decoder.channel_layout()
    };
    let output_layout = match spec.channels {
        1 => ChannelLayout::MONO,
        2 => ChannelLayout::STEREO,
        channels => return Err(AudioFinalizeError::Channels { channels }),
    };
    let output_format = ffmpeg::format::Sample::F32(ffmpeg::format::sample::Type::Packed);
    let mut resampler = software::resampling::Context::get(
        decoder.format(),
        input_layout,
        decoder.rate(),
        output_format,
        output_layout,
        spec.sample_rate,
    )
    .map_err(AudioFinalizeError::ResampleBgm)?;
    let seek_timestamp = (source_start_seconds * 1_000_000.0).round() as i64;
    input
        .seek(seek_timestamp, ..seek_timestamp)
        .map_err(AudioFinalizeError::DecodeBgm)?;
    decoder.flush();

    let source_end_seconds = source_start_seconds + duration_seconds;
    let mut decoded = frame::Audio::empty();
    let mut fallback_seconds = source_start_seconds;
    let mut finished = false;
    for (packet_stream, packet) in input.packets() {
        if packet_stream.index() != stream_index {
            continue;
        }
        decoder
            .send_packet(&packet)
            .map_err(AudioFinalizeError::DecodeBgm)?;
        if drain_bgm_decoder(
            &mut decoder,
            &mut decoded,
            &mut resampler,
            mixed,
            input_time_base,
            source_start_seconds,
            source_end_seconds,
            clip_layout,
            spec,
            &mut fallback_seconds,
        )? {
            finished = true;
            break;
        }
    }
    if !finished {
        decoder.send_eof().map_err(AudioFinalizeError::DecodeBgm)?;
        let _ = drain_bgm_decoder(
            &mut decoder,
            &mut decoded,
            &mut resampler,
            mixed,
            input_time_base,
            source_start_seconds,
            source_end_seconds,
            clip_layout,
            spec,
            &mut fallback_seconds,
        )?;
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn drain_bgm_decoder(
    decoder: &mut ffmpeg::decoder::Audio,
    decoded: &mut frame::Audio,
    resampler: &mut software::resampling::Context,
    mixed: &mut File,
    input_time_base: Rational,
    source_start_seconds: f64,
    source_end_seconds: f64,
    clip_layout: AudioClipLayout,
    spec: AudioSpec,
    fallback_seconds: &mut f64,
) -> Result<bool, AudioFinalizeError> {
    while decoder.receive_frame(decoded).is_ok() {
        let frame_start_seconds = decoded
            .timestamp()
            .map(|timestamp| timestamp as f64 * f64::from(input_time_base))
            .unwrap_or(*fallback_seconds);
        let capacity = ((decoded.samples() as u64 * u64::from(spec.sample_rate)
            / u64::from(decoded.rate().max(1)))
            + 256) as usize;
        let output_layout = match spec.channels {
            1 => ChannelLayout::MONO,
            2 => ChannelLayout::STEREO,
            channels => return Err(AudioFinalizeError::Channels { channels }),
        };
        let mut converted = frame::Audio::new(
            ffmpeg::format::Sample::F32(ffmpeg::format::sample::Type::Packed),
            capacity.max(1),
            output_layout,
        );
        converted.set_rate(spec.sample_rate);
        resampler
            .run(decoded, &mut converted)
            .map_err(AudioFinalizeError::ResampleBgm)?;
        let converted_frames = converted.samples();
        let frame_end_seconds =
            frame_start_seconds + converted_frames as f64 / f64::from(spec.sample_rate);
        *fallback_seconds = frame_end_seconds;
        if frame_start_seconds >= source_end_seconds {
            return Ok(true);
        }
        let overlap_start = frame_start_seconds.max(source_start_seconds);
        let overlap_end = frame_end_seconds.min(source_end_seconds);
        if overlap_start < overlap_end {
            let source_frame =
                seconds_to_frames(overlap_start - frame_start_seconds, spec.sample_rate)? as usize;
            let mut frames =
                seconds_to_frames(overlap_end - overlap_start, spec.sample_rate)? as usize;
            frames = frames.min(converted_frames.saturating_sub(source_frame));
            let local_frame =
                seconds_to_frames(overlap_start - source_start_seconds, spec.sample_rate)?;
            let destination_frame = clip_layout
                .output_start_frames
                .checked_add(local_frame)
                .ok_or(AudioFinalizeError::TimelineTooLarge)?;
            let channels = usize::from(spec.channels);
            let samples = converted.plane::<f32>(0);
            add_samples(
                mixed,
                destination_frame,
                &samples[source_frame * channels..(source_frame + frames) * channels],
                spec.channels,
                clip_layout,
                local_frame,
            )?;
        }
    }
    Ok(false)
}

fn mix_chunk(
    mixed: &mut File,
    chunk: &SidecarChunk,
    clips: &[FinalizeClip],
    clip_layout: &[AudioClipLayout],
    sample_rate: u32,
    channels: u16,
    exclude_captured_bgm: bool,
    sidecar: &Path,
) -> Result<(), AudioFinalizeError> {
    if !(1..=3).contains(&chunk.bus_id) {
        return Err(invalid(sidecar, format!("unknown bus id {}", chunk.bus_id)));
    }
    let channel_count = usize::from(channels);
    let chunk_frames = chunk.samples.len() / channel_count;
    let chunk_start = nanos_to_frames(chunk.media_time_nanos, sample_rate);
    let chunk_end = chunk_start.saturating_add(chunk_frames as u64);
    if exclude_captured_bgm && chunk.bus_id == 3 {
        return Ok(());
    }
    for (clip, layout) in clips.iter().zip(clip_layout) {
        let clip_start = seconds_to_frames(clip.start_seconds, sample_rate)?;
        let clip_end = clip_start.saturating_add(layout.clip_frames);
        let overlap_start = chunk_start.max(clip_start);
        let overlap_end = chunk_end.min(clip_end);
        if overlap_start < overlap_end {
            let source_frame = (overlap_start - chunk_start) as usize;
            let local_frame = overlap_start - clip_start;
            let destination_frame = layout
                .output_start_frames
                .checked_add(local_frame)
                .ok_or(AudioFinalizeError::TimelineTooLarge)?;
            let frames = (overlap_end - overlap_start) as usize;
            add_samples(
                mixed,
                destination_frame,
                &chunk.samples
                    [source_frame * channel_count..(source_frame + frames) * channel_count],
                channels,
                *layout,
                local_frame,
            )?;
        }
    }
    Ok(())
}

fn add_samples(
    mixed: &mut File,
    destination_frame: u64,
    samples: &[f32],
    channels: u16,
    clip_layout: AudioClipLayout,
    clip_local_start_frame: u64,
) -> Result<(), AudioFinalizeError> {
    let byte_offset = destination_frame
        .checked_mul(u64::from(channels))
        .and_then(|value| value.checked_mul(4))
        .ok_or(AudioFinalizeError::TimelineTooLarge)?;
    let mut bytes = vec![0_u8; samples.len() * 4];
    mixed
        .seek(SeekFrom::Start(byte_offset))
        .and_then(|_| mixed.read_exact(&mut bytes))
        .map_err(|source| AudioFinalizeError::Io {
            path: PathBuf::from("mixed PCM workspace"),
            source,
        })?;
    for (index, sample) in samples.iter().enumerate() {
        let offset = index * 4;
        let existing = f32::from_le_bytes(bytes[offset..offset + 4].try_into().unwrap());
        let local_frame = clip_local_start_frame + index as u64 / u64::from(channels);
        let gain = clip_layout.gain_at(local_frame);
        // Float headroom until ALL buses are mixed. Clamping per contribution
        // makes overlap order-dependent (0.8 + 0.8 - 0.8 must still be 0.8).
        bytes[offset..offset + 4].copy_from_slice(&(existing + sample * gain).to_le_bytes());
    }
    mixed
        .seek(SeekFrom::Start(byte_offset))
        .and_then(|_| mixed.write_all(&bytes))
        .map_err(|source| AudioFinalizeError::Io {
            path: PathBuf::from("mixed PCM workspace"),
            source,
        })
}

fn smooth_sfx_seams(
    mixed: &mut File,
    clips: &[FinalizeClip],
    layout: &[AudioClipLayout],
    sample_rate: u32,
    channels: u16,
) -> Result<(), AudioFinalizeError> {
    let path = Path::new("mixed PCM workspace");
    let channel_count = usize::from(channels);
    for index in 1..clips.len() {
        let previous = &clips[index - 1];
        let next = &clips[index];
        if !next.seamless_from_previous
            || (next.source == previous.source
                && (next.start_seconds - previous.start_seconds - previous.duration_seconds).abs()
                    <= CUT_GAP_SECONDS)
        {
            continue; // Already faded or just a metadata split, not an audio cut.
        }
        let frames = (u64::from(sample_rate) / 1_000)
            .min(layout[index - 1].clip_frames / 2)
            .min(layout[index].clip_frames / 2) as usize;
        if frames < 2 {
            continue;
        }
        let boundary = layout[index].output_start_frames;
        if boundary < frames as u64 {
            continue;
        }
        let offset = (boundary - frames as u64) * u64::from(channels) * 4;
        let mut bytes = vec![0; frames * 2 * channel_count * 4];
        mixed
            .seek(SeekFrom::Start(offset))
            .and_then(|_| mixed.read_exact(&mut bytes))
            .map_err(|e| io_error(path, e))?;
        let sample = |frame: usize, channel: usize| {
            let at = (frame * channel_count + channel) * 4;
            f32::from_le_bytes(bytes[at..at + 4].try_into().unwrap())
        };
        // Leave naturally continuous waveforms untouched. Only remove an edit's
        // amplitude step, with at most 1 ms on either side (not a 160 ms fade).
        let discontinuous = (0..channel_count).any(|channel| {
            let left = sample(frames - 1, channel);
            let right = sample(frames, channel);
            let slope = (left - sample(frames - 2, channel))
                .abs()
                .max((sample(frames + 1, channel) - right).abs());
            (right - left).abs() > slope * 4.0 + 0.01
        });
        if !discontinuous {
            continue;
        }
        for frame in 0..frames * 2 {
            let gain = if frame < frames {
                (frames - 1 - frame) as f32
            } else {
                (frame - frames) as f32
            } / (frames - 1) as f32;
            for channel in 0..channel_count {
                let at = (frame * channel_count + channel) * 4;
                let value = f32::from_le_bytes(bytes[at..at + 4].try_into().unwrap());
                bytes[at..at + 4].copy_from_slice(&(value * gain).to_le_bytes());
            }
        }
        mixed
            .seek(SeekFrom::Start(offset))
            .and_then(|_| mixed.write_all(&bytes))
            .map_err(|e| io_error(path, e))?;
    }
    Ok(())
}

fn read_chunk(
    reader: &mut BufReader<File>,
    path: &Path,
) -> Result<Option<SidecarChunk>, AudioFinalizeError> {
    let mut header = [0_u8; CHUNK_HEADER_BYTES];
    match reader.read(&mut header[..1]) {
        Ok(0) => return Ok(None),
        Ok(1) => {}
        Ok(_) => unreachable!(),
        Err(source) => return Err(io_error(path, source)),
    }
    reader
        .read_exact(&mut header[1..])
        .map_err(|source| io_error(path, source))?;
    let media_time_nanos = u64::from_le_bytes(header[0..8].try_into().unwrap());
    let sample_rate = u32::from_le_bytes(header[8..12].try_into().unwrap());
    let channels = u16::from_le_bytes(header[12..14].try_into().unwrap());
    let bus_id = u16::from_le_bytes(header[14..16].try_into().unwrap());
    let frames = u32::from_le_bytes(header[16..20].try_into().unwrap()) as usize;
    let sample_count = u32::from_le_bytes(header[20..24].try_into().unwrap()) as usize;
    if !(8_000..=384_000).contains(&sample_rate)
        || channels == 0
        || sample_count == 0
        || sample_count > MAX_CHUNK_SAMPLES
        || sample_count % usize::from(channels) != 0
        || frames != sample_count / usize::from(channels)
    {
        return Err(invalid(path, "invalid chunk dimensions"));
    }
    let mut bytes = vec![0_u8; sample_count * 4];
    reader
        .read_exact(&mut bytes)
        .map_err(|source| io_error(path, source))?;
    let samples: Vec<f32> = bytes
        .chunks_exact(4)
        .map(|value| f32::from_le_bytes(value.try_into().unwrap()))
        .collect();
    // FMOD standard speaker order is not FFmpeg's default channel-count layout.
    // Preserve raw sidecars and public PCM callbacks; downmix only for the stereo export.
    let samples = downmix_fmod(&samples, channels)?;
    Ok(Some(SidecarChunk {
        media_time_nanos,
        sample_rate,
        channels: 2,
        bus_id,
        samples,
    }))
}

fn downmix_fmod(samples: &[f32], channels: u16) -> Result<Vec<f32>, AudioFinalizeError> {
    if !matches!(channels, 1 | 2 | 4 | 5 | 6 | 8) {
        return Err(AudioFinalizeError::Channels { channels });
    }
    let mut stereo = Vec::with_capacity(samples.len() / channels as usize * 2);
    let surround = std::f32::consts::FRAC_1_SQRT_2;
    for frame in samples.chunks_exact(channels as usize) {
        let finite = |i: usize| if frame[i].is_finite() { frame[i] } else { 0.0 };
        let (mut left, mut right) = (finite(0), finite(if channels == 1 { 0 } else { 1 }));
        match channels {
            4 => {
                left += surround * finite(2);
                right += surround * finite(3);
            }
            5 => {
                left += surround * (finite(2) + finite(3));
                right += surround * (finite(2) + finite(4));
            }
            6 | 8 => {
                // FL FR FC LFE SL SR [BL BR]. LFE is intentionally excluded from stereo.
                left += surround * (finite(2) + finite(4));
                right += surround * (finite(2) + finite(5));
                if channels == 8 {
                    left += 0.5 * finite(6);
                    right += 0.5 * finite(7);
                }
            }
            _ => {}
        }
        stereo.extend([left, right]);
    }
    Ok(stereo)
}

fn encode_aac(
    mixed_pcm: &Path,
    output_path: &Path,
    spec: AudioSpec,
) -> Result<(), AudioFinalizeError> {
    let codec = encoder::find(codec::Id::AAC)
        .ok_or(AudioFinalizeError::MissingAac)?
        .audio()
        .map_err(AudioFinalizeError::ConfigureAac)?;
    let sample_format = ffmpeg::format::Sample::F32(ffmpeg::format::sample::Type::Planar);
    if codec
        .formats()
        .is_some_and(|mut formats| !formats.any(|format| format == sample_format))
    {
        return Err(AudioFinalizeError::UnsupportedAacFormat);
    }
    let layout = match spec.channels {
        1 => ChannelLayout::MONO,
        2 => ChannelLayout::STEREO,
        channels => return Err(AudioFinalizeError::Channels { channels }),
    };
    let _ = fs::remove_file(output_path);
    let mut output =
        format::output(output_path).map_err(|source| AudioFinalizeError::CreateAudioOutput {
            path: output_path.to_owned(),
            source,
        })?;
    let global_header = output
        .format()
        .flags()
        .contains(format::Flags::GLOBAL_HEADER);
    let mut audio = codec::context::Context::new_with_codec(*codec)
        .encoder()
        .audio()
        .map_err(AudioFinalizeError::ConfigureAac)?;
    audio.set_rate(spec.sample_rate as i32);
    audio.set_channel_layout(layout);
    audio.set_format(sample_format);
    audio.set_time_base(Rational(1, spec.sample_rate as i32));
    audio.set_bit_rate(AUDIO_BITRATE);
    if global_header {
        audio.set_flags(codec::Flags::GLOBAL_HEADER);
    }
    let mut opened = audio
        .open_as(codec)
        .map_err(AudioFinalizeError::ConfigureAac)?;
    let stream_index;
    {
        let mut stream = output
            .add_stream(codec)
            .map_err(AudioFinalizeError::ConfigureAac)?;
        stream.set_time_base(Rational(1, spec.sample_rate as i32));
        stream.set_parameters(&opened);
        stream_index = stream.index();
    }
    output
        .write_header()
        .map_err(AudioFinalizeError::ConfigureAac)?;
    let stream_time_base = output
        .stream(stream_index)
        .expect("new AAC stream disappeared")
        .time_base();
    let frame_size = opened.frame_size().max(1) as usize;
    let mut reader =
        BufReader::new(File::open(mixed_pcm).map_err(|source| io_error(mixed_pcm, source))?);
    let mut interleaved = vec![0_u8; frame_size * usize::from(spec.channels) * 4];
    let mut frame_start = 0_u64;
    while frame_start < spec.total_frames {
        interleaved.fill(0);
        let wanted_frames = (spec.total_frames - frame_start).min(frame_size as u64) as usize;
        let wanted_bytes = wanted_frames * usize::from(spec.channels) * 4;
        reader
            .read_exact(&mut interleaved[..wanted_bytes])
            .map_err(|source| io_error(mixed_pcm, source))?;
        let mut audio_frame = frame::Audio::new(sample_format, frame_size, layout);
        audio_frame.set_rate(spec.sample_rate);
        audio_frame.set_pts(Some(frame_start as i64));
        for channel in 0..usize::from(spec.channels) {
            let plane = audio_frame.plane_mut::<f32>(channel);
            plane.fill(0.0);
            for sample_index in 0..wanted_frames {
                let offset = (sample_index * usize::from(spec.channels) + channel) * 4;
                plane[sample_index] =
                    f32::from_le_bytes(interleaved[offset..offset + 4].try_into().unwrap())
                        .clamp(-1.0, 1.0);
            }
        }
        opened
            .send_frame(&audio_frame)
            .map_err(AudioFinalizeError::SendAudio)?;
        write_audio_packets(
            &mut opened,
            &mut output,
            stream_index,
            Rational(1, spec.sample_rate as i32),
            stream_time_base,
        )?;
        frame_start += wanted_frames as u64;
    }
    opened.send_eof().map_err(AudioFinalizeError::SendAudio)?;
    write_audio_packets(
        &mut opened,
        &mut output,
        stream_index,
        Rational(1, spec.sample_rate as i32),
        stream_time_base,
    )?;
    output
        .write_trailer()
        .map_err(AudioFinalizeError::AudioTrailer)
}

fn write_audio_packets(
    encoder: &mut ffmpeg::encoder::audio::Encoder,
    output: &mut format::context::Output,
    stream_index: usize,
    encoder_time_base: Rational,
    stream_time_base: Rational,
) -> Result<(), AudioFinalizeError> {
    let mut packet = Packet::empty();
    loop {
        match encoder.receive_packet(&mut packet) {
            Ok(()) => {
                packet.set_stream(stream_index);
                packet.set_position(-1);
                packet.rescale_ts(encoder_time_base, stream_time_base);
                packet
                    .write_interleaved(output)
                    .map_err(AudioFinalizeError::AudioPacket)?;
            }
            Err(ffmpeg::Error::Other { errno }) if errno == ffmpeg::error::EAGAIN => break,
            Err(ffmpeg::Error::Eof) => break,
            Err(error) => return Err(AudioFinalizeError::AudioPacket(error)),
        }
    }
    Ok(())
}

pub fn mux_video_and_audio(
    video_path: &Path,
    audio_path: &Path,
    output_path: &Path,
) -> Result<(), AudioFinalizeError> {
    let mut video_input =
        format::input(video_path).map_err(|source| AudioFinalizeError::OpenMuxInput {
            path: video_path.to_owned(),
            source,
        })?;
    let mut audio_input =
        format::input(audio_path).map_err(|source| AudioFinalizeError::OpenMuxInput {
            path: audio_path.to_owned(),
            source,
        })?;
    let video_stream = video_input
        .streams()
        .best(media::Type::Video)
        .ok_or_else(|| AudioFinalizeError::MissingMuxStream {
            path: video_path.to_owned(),
            kind: "video",
        })?;
    let audio_stream = audio_input
        .streams()
        .best(media::Type::Audio)
        .ok_or_else(|| AudioFinalizeError::MissingMuxStream {
            path: audio_path.to_owned(),
            kind: "audio",
        })?;
    let video_index = video_stream.index();
    let audio_index = audio_stream.index();
    let video_time_base = video_stream.time_base();
    let audio_time_base = audio_stream.time_base();
    let video_parameters = video_stream.parameters();
    let audio_parameters = audio_stream.parameters();
    drop(video_stream);
    drop(audio_stream);

    let _ = fs::remove_file(output_path);
    let mut output =
        format::output(output_path).map_err(|source| AudioFinalizeError::CreateMuxOutput {
            path: output_path.to_owned(),
            source,
        })?;
    let output_video_index;
    {
        let mut stream = output
            .add_stream(encoder::find(codec::Id::None))
            .map_err(AudioFinalizeError::ConfigureMux)?;
        stream.set_parameters(video_parameters);
        unsafe { (*stream.parameters().as_mut_ptr()).codec_tag = 0 };
        output_video_index = stream.index();
    }
    let output_audio_index;
    {
        let mut stream = output
            .add_stream(encoder::find(codec::Id::None))
            .map_err(AudioFinalizeError::ConfigureMux)?;
        stream.set_parameters(audio_parameters);
        unsafe { (*stream.parameters().as_mut_ptr()).codec_tag = 0 };
        output_audio_index = stream.index();
    }
    // Preserve video decoder preroll and AAC priming across the final remux.
    // Shifting negative video timestamps would reintroduce pre-trim gameplay
    // and put audio ahead of the requested death replay range.
    let mut options = ffmpeg::Dictionary::new();
    options.set("avoid_negative_ts", "disabled");
    options.set("use_editlist", "1");
    output
        .write_header_with(options)
        .map_err(AudioFinalizeError::ConfigureMux)?;
    let output_video_time_base = output
        .stream(output_video_index)
        .expect("mux video stream disappeared")
        .time_base();
    let output_audio_time_base = output
        .stream(output_audio_index)
        .expect("mux audio stream disappeared")
        .time_base();

    let mut video_packet = next_packet(&mut video_input, video_index)?;
    let mut audio_packet = next_packet(&mut audio_input, audio_index)?;
    while video_packet.is_some() || audio_packet.is_some() {
        let take_video = match (&video_packet, &audio_packet) {
            (Some(video), Some(audio)) => {
                packet_seconds(video, video_time_base) <= packet_seconds(audio, audio_time_base)
            }
            (Some(_), None) => true,
            (None, Some(_)) => false,
            (None, None) => break,
        };
        if take_video {
            let mut packet = video_packet.take().unwrap();
            packet.rescale_ts(video_time_base, output_video_time_base);
            packet.set_stream(output_video_index);
            packet.set_position(-1);
            packet
                .write_interleaved(&mut output)
                .map_err(AudioFinalizeError::MuxPacket)?;
            video_packet = next_packet(&mut video_input, video_index)?;
        } else {
            let mut packet = audio_packet.take().unwrap();
            packet.rescale_ts(audio_time_base, output_audio_time_base);
            packet.set_stream(output_audio_index);
            packet.set_position(-1);
            packet
                .write_interleaved(&mut output)
                .map_err(AudioFinalizeError::MuxPacket)?;
            audio_packet = next_packet(&mut audio_input, audio_index)?;
        }
    }
    output
        .write_trailer()
        .map_err(AudioFinalizeError::MuxTrailer)
}

fn next_packet(
    input: &mut format::context::Input,
    stream_index: usize,
) -> Result<Option<Packet>, AudioFinalizeError> {
    loop {
        let mut packet = Packet::empty();
        match packet.read(input) {
            Ok(()) if packet.stream() == stream_index => return Ok(Some(packet)),
            Ok(()) => continue,
            Err(ffmpeg::Error::Eof) => return Ok(None),
            Err(error) => return Err(AudioFinalizeError::MuxPacket(error)),
        }
    }
}

fn packet_seconds(packet: &Packet, time_base: Rational) -> f64 {
    packet.dts().or_else(|| packet.pts()).unwrap_or(0) as f64 * f64::from(time_base)
}

fn seconds_to_frames(seconds: f64, sample_rate: u32) -> Result<u64, AudioFinalizeError> {
    let frames = seconds * f64::from(sample_rate);
    if !frames.is_finite() || frames < 0.0 || frames > u64::MAX as f64 {
        return Err(AudioFinalizeError::TimelineTooLarge);
    }
    Ok(frames.round() as u64)
}

fn nanos_to_frames(nanos: u64, sample_rate: u32) -> u64 {
    ((u128::from(nanos) * u128::from(sample_rate)) / 1_000_000_000_u128).min(u128::from(u64::MAX))
        as u64
}

fn io_error(path: &Path, source: std::io::Error) -> AudioFinalizeError {
    AudioFinalizeError::Io {
        path: path.to_owned(),
        source,
    }
}

fn invalid(path: &Path, detail: impl Into<String>) -> AudioFinalizeError {
    AudioFinalizeError::InvalidSidecar {
        path: path.to_owned(),
        detail: detail.into(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{AudioChunk, write_audio_chunk};
    use std::io::{BufWriter, Write};

    #[test]
    fn fmod_surround_downmix_preserves_speaker_sides_and_excludes_lfe() {
        for channels in [1, 2, 4, 5, 6, 8] {
            let mut pcm = vec![0.0; channels];
            pcm[0] = 0.25;
            let stereo = downmix_fmod(&pcm, channels as u16).unwrap();
            assert_eq!(stereo[0], 0.25);
            assert_eq!(stereo[1], if channels == 1 { 0.25 } else { 0.0 });
        }
        for (channel, left, right) in [
            (2, 0.70710677, 0.70710677),
            (3, 0.0, 0.0),
            (4, 0.70710677, 0.0),
            (5, 0.0, 0.70710677),
            (6, 0.5, 0.0),
            (7, 0.0, 0.5),
        ] {
            let mut pcm = vec![0.0; 8];
            pcm[channel] = 1.0;
            assert_eq!(downmix_fmod(&pcm, 8).unwrap(), vec![left, right]);
        }
        assert!(downmix_fmod(&[0.0; 7], 7).is_err());
        assert_eq!(
            downmix_fmod(&[f32::NAN, f32::INFINITY], 2).unwrap(),
            vec![0.0, 0.0]
        );
    }

    fn journal(path: &Path, extra: &[(u64, &str, &str)]) {
        let mut lines = vec![r#"{"type":"header","version":1}"#.to_string()];
        for &(time, kind, event) in std::iter::once(&(0, "snapshot", "music/a")).chain(extra) {
            lines.push(
                serde_json::json!({"type":"event","time_nanos":time,"kind":kind,"track":"main",
                "event":event,"timeline_milliseconds":0})
                .to_string(),
            );
        }
        lines.push(r#"{"type":"end","complete":true}"#.to_string());
        fs::write(path.with_extension("music.jsonl"), lines.join("\n")).unwrap();
    }
    fn clip(start: f64, duration: f64) -> FinalizeClip {
        FinalizeClip {
            source: "run.mkv".into(),
            start_seconds: start,
            duration_seconds: duration,
            music_event: "wrong-per-clip-metadata".into(),
            music_timeline_milliseconds: 99999,
            seamless_from_previous: true,
            bgm_follows_video: false,
        }
    }

    #[test]
    fn overlapping_buses_keep_float_headroom_until_the_complete_mix() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("mix.f32");
        let layout = AudioClipLayout {
            output_start_frames: 0,
            clip_frames: 64,
            fade_in_frames: 0,
            fade_out_frames: 0,
        };
        for values in [[0.8, 0.8, -0.8], [-0.8, 0.8, 0.8], [0.8, -0.8, 0.8]] {
            fs::write(&path, vec![0; 64 * 2 * 4]).unwrap();
            let mut file = OpenOptions::new()
                .read(true)
                .write(true)
                .open(&path)
                .unwrap();
            for value in values {
                add_samples(&mut file, 0, &[value; 128], 2, layout, 0).unwrap();
            }
            drop(file);
            for bytes in fs::read(&path).unwrap().chunks_exact(4) {
                assert!((f32::from_le_bytes(bytes.try_into().unwrap()) - 0.8).abs() < 1e-6);
            }
        }
    }

    #[test]
    fn sfx_declick_only_touches_one_ms_either_side_of_a_discontinuous_edit() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("mix.f32");
        for continuous in [false, true] {
            let clips = [
                clip(0.0, 1.0),
                clip(if continuous { 1.0 } else { 2.0 }, 1.0),
            ];
            let layout = audio_timeline_layout(&clips, 48000).unwrap();
            let samples: Vec<f32> = (0..96000)
                .map(|i| if i < 48000 { 0.8 } else { -0.8 })
                .collect();
            let bytes: Vec<u8> = samples.iter().flat_map(|v| v.to_le_bytes()).collect();
            fs::write(&path, &bytes).unwrap();
            let mut file = OpenOptions::new()
                .read(true)
                .write(true)
                .open(&path)
                .unwrap();
            smooth_sfx_seams(&mut file, &clips, &layout, 48000, 1).unwrap();
            drop(file);
            let edited: Vec<f32> = fs::read(&path)
                .unwrap()
                .chunks_exact(4)
                .map(|b| f32::from_le_bytes(b.try_into().unwrap()))
                .collect();
            if continuous {
                assert_eq!(edited, samples);
            } else {
                assert_eq!(edited.len(), 96000);
                assert_eq!(&edited[..47952], &samples[..47952]);
                assert_eq!(&edited[48048..], &samples[48048..]);
                assert_eq!(edited[47999], 0.0);
                assert_eq!(edited[48000], 0.0);
                assert!(edited.windows(2).all(|p| (p[1] - p[0]).abs() < 0.02));
            }
        }
        // A seamless waveform at a real cut must not get an unnecessary dip.
        let clips = [clip(0.0, 1.0), clip(2.0, 1.0)];
        let samples: Vec<f32> = (0..96000)
            .map(|i| ((i as f32) * 0.02).sin() * 0.2)
            .collect();
        let bytes: Vec<u8> = samples.iter().flat_map(|v| v.to_le_bytes()).collect();
        fs::write(&path, &bytes).unwrap();
        let mut file = OpenOptions::new()
            .read(true)
            .write(true)
            .open(&path)
            .unwrap();
        smooth_sfx_seams(
            &mut file,
            &clips,
            &audio_timeline_layout(&clips, 48000).unwrap(),
            48000,
            1,
        )
        .unwrap();
        drop(file);
        assert_eq!(fs::read(&path).unwrap(), bytes);
    }

    #[test]
    fn journal_bgm_does_not_jump_at_video_cuts_or_metadata_splits() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("run.mkv.bgmchunks");
        journal(&path, &[]);
        let result = journal_segments(
            &path,
            &[clip(0.0, 1.0), clip(2.0, 0.5), clip(2.5, 0.5)],
            8000,
        )
        .unwrap();
        assert_eq!(
            result
                .iter()
                .map(|s| s.captured_source_start_frames)
                .collect::<Vec<_>>(),
            [0, 8000, 12000]
        );
        assert_eq!(result[1].music_event, "music/a");
    }

    #[test]
    fn journal_preserves_in_clip_switches_and_same_song_restart_inside_removed_range() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("run.mkv.bgmchunks");
        journal(
            &path,
            &[
                (1_500_000_000, "start", "music/a"),
                (2_500_000_000, "switch", "music/b"),
            ],
        );
        let result = journal_segments(&path, &[clip(0.0, 1.0), clip(2.0, 1.0)], 8000).unwrap();
        assert_eq!(result.len(), 3);
        assert_eq!(result[1].captured_source_start_frames, 16000); // not old take at 1s
        assert_eq!(result[2].output_start_frames, 12000);
        assert_eq!(result[2].captured_source_start_frames, 20000);
        assert_eq!(result[2].music_event, "music/b");
    }

    #[test]
    fn incomplete_music_journal_is_never_used_for_reconstruction() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("run.mkv.bgmchunks");
        fs::write(
            path.with_extension("music.jsonl"),
            "{\"type\":\"header\",\"version\":1}\n",
        )
        .unwrap();
        assert!(journal_segments(&path, &[clip(0.0, 1.0)], 8000).is_err());
    }

    #[test]
    fn rendered_bgm_samples_remain_continuous_while_sfx_are_cut() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("run.mkv.bgmchunks");
        let mixed = dir.path().join("mixed.f32");
        journal(&path, &[]);
        let mut writer = BufWriter::new(File::create(&path).unwrap());
        writer.write_all(SIDECAR_MAGIC).unwrap();
        for second in 0..4 {
            let samples: Vec<f32> = (0..8000)
                .flat_map(|i| [((second * 8000 + i) as f32) / 100000.0; 2])
                .collect();
            write_audio_chunk(
                &mut writer,
                &AudioChunk {
                    media_time_nanos: second * 1_000_000_000,
                    sample_rate: 8000,
                    channels: 2,
                    bus_id: 3,
                    samples,
                },
            )
            .unwrap();
        }
        writer.flush().unwrap();
        let clips = [clip(0.0, 1.0), clip(3.0, 1.0)];
        let spec = AudioSpec {
            sample_rate: 8000,
            channels: 2,
            total_frames: 16000,
        };
        create_empty_mix(&mixed, spec).unwrap();
        mix_captured_bgm(&path, &mixed, &clips, &HashMap::new(), spec).unwrap();
        let bytes = fs::read(&mixed).unwrap();
        for frame in [7999, 8000, 8001, 15999] {
            let sample = f32::from_le_bytes(bytes[frame * 8..frame * 8 + 4].try_into().unwrap());
            assert!((sample - frame as f32 / 100000.0).abs() < 1e-6);
        }
        create_empty_mix(&mixed, spec).unwrap();
        mix_separate_bgm_with_video(&path, &mixed, &clips, spec).unwrap();
        let bytes = fs::read(&mixed).unwrap();
        assert_eq!(
            f32::from_le_bytes(bytes[8000 * 8..8000 * 8 + 4].try_into().unwrap()),
            0.24
        );
    }

    #[test]
    fn only_sensitive_room_segments_follow_video_in_journal_and_legacy_planners() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("run.mkv.bgmchunks");
        // Metadata ranges shorter than one PCM sample need no segment/previous cursor.
        assert_eq!(
            post_mix_segments(&[clip(0.0, 0.000001), clip(0.000001, 1.0)], 8000)
                .unwrap()
                .len(),
            1
        );
        let mut clips = vec![
            clip(0.0, 1.0),
            clip(2.0, 1.0),
            clip(4.0, 1.0),
            clip(6.0, 1.0),
            clip(8.0, 1.0),
            clip(10.0, 1.0),
            clip(11.0, 1.0),
        ];
        for c in &mut clips {
            c.music_event = "music/a".into();
            c.music_timeline_milliseconds = (c.start_seconds * 1000.0) as i64;
        }
        clips[2].bgm_follows_video = true;
        clips[3].bgm_follows_video = true;
        // Ordinary edits before AND after the sensitive room remain continuous. The
        // last clip is a contiguous metadata/room split, not another edit.
        let expected = [0, 8000, 32000, 48000, 56000, 64000, 72000];
        for with_journal in [false, true] {
            if with_journal {
                journal(&path, &[]);
            }
            let result = journal_segments(&path, &clips, 8000).unwrap();
            assert_eq!(
                result
                    .iter()
                    .map(|s| s.captured_source_start_frames)
                    .collect::<Vec<_>>(),
                expected
            );
            assert_eq!(
                result
                    .iter()
                    .map(|s| s.bgm_follows_video)
                    .collect::<Vec<_>>(),
                [false, false, true, true, false, false, false]
            );
        }
    }

    #[test]
    fn hybrid_room_policy_renders_exact_samples_through_production_audio_dispatch() {
        ffmpeg::init().unwrap();
        let dir = tempfile::tempdir().unwrap();
        let sfx = dir.path().join("run.mkv.sfxchunks");
        let bgm = sfx.with_extension("bgmchunks");
        let mixed = dir.path().join("mixed.f32");
        journal(&bgm, &[]);
        for (path, bus) in [(&sfx, 1), (&bgm, 3)] {
            let mut writer = BufWriter::new(File::create(path).unwrap());
            writer.write_all(SIDECAR_MAGIC).unwrap();
            for second in 0..12 {
                let samples = (0..8000)
                    .flat_map(|i| {
                        let frame = (second * 8000 + i) as f32;
                        [if bus == 3 {
                            frame / 200000.0
                        } else {
                            frame / 2000000.0
                        }; 2]
                    })
                    .collect();
                write_audio_chunk(
                    &mut writer,
                    &AudioChunk {
                        media_time_nanos: second * 1_000_000_000,
                        sample_rate: 8000,
                        channels: 2,
                        bus_id: bus,
                        samples,
                    },
                )
                .unwrap();
            }
            writer.flush().unwrap();
        }
        let mut clips = vec![
            clip(0.0, 1.0),
            clip(2.0, 1.0),
            clip(4.0, 1.0),
            clip(6.0, 1.0),
            clip(8.0, 1.0),
            clip(10.0, 1.0),
            clip(11.0, 1.0),
        ];
        clips[2].bgm_follows_video = true;
        clips[3].bgm_follows_video = true;
        let sources = [0, 16000, 32000, 48000, 64000, 80000, 88000];
        for reconstruct in [true, false] {
            assert!(
                build_audio_track(
                    &sfx,
                    &clips,
                    &mixed,
                    &dir.path().join("out.m4a"),
                    reconstruct,
                    None
                )
                .unwrap()
            );
            let bytes = fs::read(&mixed).unwrap();
            assert_eq!(bytes.len(), 7 * 8000 * 8);
            let music_sources = if reconstruct {
                [0, 8000, 32000, 48000, 56000, 64000, 72000]
            } else {
                sources
            };
            for index in 0..7 {
                for offset in [0, 1, 4000, 7999] {
                    let frame = index * 8000 + offset;
                    let actual =
                        f32::from_le_bytes(bytes[frame * 8..frame * 8 + 4].try_into().unwrap());
                    let expected = (sources[index] + offset) as f32 / 2000000.0
                        + (music_sources[index] + offset) as f32 / 200000.0;
                    assert!(
                        (actual - expected).abs() < 1e-6,
                        "reconstruct={reconstruct}, clip={index}, offset={offset}: {actual} != {expected}"
                    );
                }
            }
        }
        // Even an external mapping must not substitute the sensitive room's PCM.
        let sensitive = &clips[2..4];
        let spec = AudioSpec {
            sample_rate: 8000,
            channels: 2,
            total_frames: 16000,
        };
        let mapping =
            HashMap::from([("music/a".into(), dir.path().join("must-not-be-opened.wav"))]);
        create_empty_mix(&mixed, spec).unwrap();
        mix_captured_bgm(&bgm, &mixed, sensitive, &mapping, spec).unwrap();
        mix_bgm_tracks(&bgm, &mixed, sensitive, &mapping, spec).unwrap();
        let bytes = fs::read(&mixed).unwrap();
        for (frame, source) in [(0, 32000), (7999, 39999), (8000, 48000), (15999, 55999)] {
            let actual = f32::from_le_bytes(bytes[frame * 8..frame * 8 + 4].try_into().unwrap());
            assert!((actual - source as f32 / 200000.0).abs() < 1e-6);
        }
    }

    #[test]
    fn post_mix_bgm_stays_continuous_across_an_edit_cut() {
        let clips = [
            FinalizeClip {
                source: "run.mkv".to_owned(),
                start_seconds: 0.0,
                duration_seconds: 1.0,
                music_event: "event:/music/test".to_owned(),
                music_timeline_milliseconds: 0,
                seamless_from_previous: false,
                bgm_follows_video: false,
            },
            FinalizeClip {
                source: "run.mkv".to_owned(),
                start_seconds: 3.0,
                duration_seconds: 1.0,
                music_event: "event:/music/test".to_owned(),
                music_timeline_milliseconds: 3_000,
                seamless_from_previous: false,
                bgm_follows_video: false,
            },
        ];
        let segments = post_mix_segments(&clips, 1_000).unwrap();
        assert_eq!(segments.len(), 2);
        assert_eq!(segments[0].output_start_frames, 0);
        assert_eq!(segments[0].frames, 1_000);
        assert_eq!(segments[1].output_start_frames, 1_000);
        assert_eq!(segments[1].captured_source_start_frames, 1_000);
        assert!((segments[1].mapped_source_start_seconds - 1.0).abs() < 1e-9);
    }

    #[test]
    fn post_mix_bgm_preserves_real_event_changes() {
        let clips = [
            FinalizeClip {
                source: "run.mkv".to_owned(),
                start_seconds: 0.0,
                duration_seconds: 1.0,
                music_event: "event:/music/a".to_owned(),
                music_timeline_milliseconds: 0,
                seamless_from_previous: false,
                bgm_follows_video: false,
            },
            FinalizeClip {
                source: "run.mkv".to_owned(),
                start_seconds: 3.0,
                duration_seconds: 1.0,
                music_event: "event:/music/b".to_owned(),
                music_timeline_milliseconds: 500,
                seamless_from_previous: false,
                bgm_follows_video: false,
            },
        ];
        let segments = post_mix_segments(&clips, 1_000).unwrap();
        assert_eq!(segments[1].captured_source_start_frames, 3_000);
        assert!((segments[1].mapped_source_start_seconds - 0.5).abs() < 1e-9);
    }

    #[test]
    fn post_mix_bgm_resumes_after_a_paused_music_bus() {
        let clips = [
            FinalizeClip {
                source: "run.mkv".to_owned(),
                start_seconds: 0.0,
                duration_seconds: 1.0,
                music_event: "event:/music/test".to_owned(),
                music_timeline_milliseconds: 0,
                seamless_from_previous: false,
                bgm_follows_video: false,
            },
            FinalizeClip {
                source: "run.mkv".to_owned(),
                start_seconds: 3.0,
                duration_seconds: 1.0,
                music_event: "event:/music/test".to_owned(),
                music_timeline_milliseconds: 750,
                seamless_from_previous: false,
                bgm_follows_video: false,
            },
        ];
        let segments = post_mix_segments(&clips, 1_000).unwrap();
        assert_eq!(segments[1].captured_source_start_frames, 3_250);
        assert!((segments[1].mapped_source_start_seconds - 1.0).abs() < 1e-9);
    }

    #[test]
    fn overlapping_bus_chunks_are_mixed_before_timeline_encoding() {
        let directory = tempfile::tempdir().unwrap();
        let sidecar = directory.path().join("room.mkv.sfxchunks");
        let mixed = directory.path().join("mixed.f32");
        let mut writer = BufWriter::new(File::create(&sidecar).unwrap());
        writer.write_all(SIDECAR_MAGIC).unwrap();
        for (bus_id, value) in [(1_u16, 0.2_f32), (2_u16, 0.3_f32)] {
            write_audio_chunk(
                &mut writer,
                &AudioChunk {
                    media_time_nanos: 0,
                    sample_rate: 8_000,
                    channels: 2,
                    bus_id,
                    samples: vec![value; 8],
                },
            )
            .unwrap();
        }
        writer.flush().unwrap();

        let spec = render_mix(
            &sidecar,
            &[FinalizeClip {
                source: "room.mkv".to_owned(),
                start_seconds: 1.0 / 8_000.0,
                duration_seconds: 2.0 / 8_000.0,
                music_event: String::new(),
                music_timeline_milliseconds: 0,
                seamless_from_previous: false,
                bgm_follows_video: false,
            }],
            &mixed,
            false,
        )
        .unwrap()
        .unwrap();
        assert_eq!(spec.total_frames, 2);
        let bytes = fs::read(mixed).unwrap();
        let values: Vec<f32> = bytes
            .chunks_exact(4)
            .map(|value| f32::from_le_bytes(value.try_into().unwrap()))
            .collect();
        assert_eq!(values.len(), 4);
        assert!(values.iter().all(|value| (*value - 0.5).abs() < 1e-6));
    }

    #[test]
    fn disjoint_clips_hard_cut_audio_on_the_same_timeline_as_video() {
        let directory = tempfile::tempdir().unwrap();
        let sidecar = directory.path().join("room.mkv.sfxchunks");
        let mixed = directory.path().join("mixed.f32");
        let mut writer = BufWriter::new(File::create(&sidecar).unwrap());
        writer.write_all(SIDECAR_MAGIC).unwrap();
        for (second, value) in [0.8_f32, 0.0, -0.8].into_iter().enumerate() {
            write_audio_chunk(
                &mut writer,
                &AudioChunk {
                    media_time_nanos: second as u64 * 1_000_000_000,
                    sample_rate: 8_000,
                    channels: 2,
                    bus_id: 1,
                    samples: vec![value; 8_000 * 2],
                },
            )
            .unwrap();
        }
        writer.flush().unwrap();

        let clips = [
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
                start_seconds: 2.0,
                duration_seconds: 1.0,
                music_event: String::new(),
                music_timeline_milliseconds: 0,
                seamless_from_previous: false,
                bgm_follows_video: false,
            },
        ];
        let spec = render_mix(&sidecar, &clips, &mixed, false)
            .unwrap()
            .unwrap();
        assert_eq!(spec.total_frames, 16_000);
        let values: Vec<f32> = fs::read(mixed)
            .unwrap()
            .chunks_exact(8)
            .map(|frame| f32::from_le_bytes(frame[0..4].try_into().unwrap()))
            .collect();
        assert!((values[6_000] - 0.8).abs() < 1e-6);
        assert!((values[7_000] - 0.8).abs() < 1e-6);
        assert!((values[8_000] + 0.8).abs() < 1e-6);
    }

    #[test]
    fn seamless_clip_keeps_full_audio_gain_at_the_pause_cut() {
        let clips = [
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
                start_seconds: 2.0,
                duration_seconds: 1.0,
                music_event: String::new(),
                music_timeline_milliseconds: 0,
                seamless_from_previous: true,
                bgm_follows_video: false,
            },
        ];
        let layout = audio_timeline_layout(&clips, 8_000).unwrap();
        assert_eq!(layout[0].fade_out_frames, 0);
        assert_eq!(layout[1].fade_in_frames, 0);
        assert_eq!(layout[1].output_start_frames, 8_000);
        assert_eq!(layout[0].gain_at(7_999), 1.0);
        assert_eq!(layout[1].gain_at(0), 1.0);
    }

    #[test]
    fn mapped_bgm_replaces_captured_music_without_doubling_it() {
        let directory = tempfile::tempdir().unwrap();
        let sidecar = directory.path().join("run.mkv.sfxchunks");
        let mixed = directory.path().join("mixed.f32");
        let mut writer = BufWriter::new(File::create(&sidecar).unwrap());
        writer.write_all(SIDECAR_MAGIC).unwrap();
        for (bus_id, value) in [(1_u16, 0.2_f32), (3_u16, 0.4_f32)] {
            write_audio_chunk(
                &mut writer,
                &AudioChunk {
                    media_time_nanos: 0,
                    sample_rate: 8_000,
                    channels: 2,
                    bus_id,
                    samples: vec![value; 8],
                },
            )
            .unwrap();
        }
        writer.flush().unwrap();

        render_mix(
            &sidecar,
            &[FinalizeClip {
                source: "run.mkv".to_owned(),
                start_seconds: 0.0,
                duration_seconds: 4.0 / 8_000.0,
                music_event: "event:/music/test".to_owned(),
                music_timeline_milliseconds: 0,
                seamless_from_previous: false,
                bgm_follows_video: false,
            }],
            &mixed,
            true,
        )
        .unwrap();

        let bytes = fs::read(mixed).unwrap();
        let values: Vec<f32> = bytes
            .chunks_exact(4)
            .map(|value| f32::from_le_bytes(value.try_into().unwrap()))
            .collect();
        assert!(values.iter().all(|value| (*value - 0.2).abs() < 1e-6));
    }

    #[test]
    fn mapped_bgm_is_decoded_from_its_saved_timeline_position() {
        if std::env::var_os("MQOL_TEST_FFMPEG").is_none() {
            return;
        }
        ffmpeg::init().unwrap();
        let directory = tempfile::tempdir().unwrap();
        let source_pcm = directory.path().join("source.f32");
        let source_audio = directory.path().join("bgm.m4a");
        let sample_rate = 48_000_u32;
        let channels = 2_u16;
        let total_frames = sample_rate as u64;
        let mut pcm = Vec::with_capacity(total_frames as usize * usize::from(channels) * 4);
        for frame in 0..total_frames {
            let value = if frame < total_frames / 2 {
                0.0
            } else {
                (frame as f32 * 440.0 * std::f32::consts::TAU / sample_rate as f32).sin() * 0.25
            };
            for _ in 0..channels {
                pcm.extend_from_slice(&value.to_le_bytes());
            }
        }
        fs::write(&source_pcm, pcm).unwrap();
        encode_aac(
            &source_pcm,
            &source_audio,
            AudioSpec {
                sample_rate,
                channels,
                total_frames,
            },
        )
        .unwrap();

        let event_map = directory.path().join("bgm-map.json");
        fs::write(&event_map, r#"{"event:/test":"bgm.m4a"}"#).unwrap();
        let output_mix = directory.path().join("output.f32");
        let output_audio = directory.path().join("output.m4a");
        let clips = [FinalizeClip {
            source: "room.mkv".to_owned(),
            start_seconds: 0.0,
            duration_seconds: 0.20,
            music_event: "event:/test".to_owned(),
            music_timeline_milliseconds: 600,
            seamless_from_previous: false,
            bgm_follows_video: false,
        }];
        assert!(
            build_audio_track(
                &directory.path().join("missing.sfxchunks"),
                &clips,
                &output_mix,
                &output_audio,
                true,
                Some(&event_map),
            )
            .unwrap()
        );
        let bytes = fs::read(output_mix).unwrap();
        let samples: Vec<f32> = bytes
            .chunks_exact(4)
            .map(|sample| f32::from_le_bytes(sample.try_into().unwrap()))
            .collect();
        let rms = (samples
            .iter()
            .map(|sample| f64::from(*sample) * f64::from(*sample))
            .sum::<f64>()
            / samples.len() as f64)
            .sqrt();
        assert!(rms > 0.05, "unexpected reconstructed BGM RMS {rms}");
        assert!(fs::metadata(output_audio).unwrap().len() > 1_000);
    }
}
