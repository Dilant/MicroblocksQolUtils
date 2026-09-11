#![deny(unsafe_op_in_unsafe_fn)]

use std::collections::{HashMap, VecDeque};
use std::ffi::{c_char, c_void};
use std::fs::File;
use std::io::{Read, Seek, SeekFrom, Write};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::PathBuf;
use std::ptr;
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Condvar, Mutex, OnceLock};
use std::thread::{self, JoinHandle};

use serde::{Deserialize, Serialize};
use thiserror::Error;

#[cfg(windows)]
mod d3d11_readback;
#[cfg(windows)]
mod dwrite_raster;
mod raster;
mod sdl_readback;

#[cfg(feature = "ffmpeg")]
mod encoder;
#[cfg(feature = "ffmpeg")]
mod finalizer;
#[cfg(feature = "ffmpeg")]
mod finalizer_audio;
#[cfg(feature = "ffmpeg")]
mod finalizer_copy;

const ABI_VERSION: u32 = 11;
const OK: i32 = 0;
const ERR_INVALID_ARGUMENT: i32 = -1;
const ERR_NOT_FOUND: i32 = -2;
const ERR_ALREADY_RUNNING: i32 = -3;
const ERR_PLATFORM: i32 = -5;
const ERR_CAPTURE: i32 = -6;
const ERR_PANIC: i32 = -127;
const AUDIO_QUEUE_CAPACITY: usize = 256;
const AUDIO_MAX_SAMPLES_PER_CHUNK: usize = 16_384;
const AUDIO_BUS_COUNT: usize = 3;

static NEXT_HANDLE: AtomicU64 = AtomicU64::new(1);
static SESSIONS: OnceLock<Mutex<HashMap<u64, Arc<CaptureSession>>>> = OnceLock::new();
static LAST_ERROR: OnceLock<Mutex<String>> = OnceLock::new();

#[derive(Debug, Clone, Deserialize)]
#[serde(default)]
pub struct CaptureConfig {
    pub fps: u32,
    pub queue_capacity: usize,
    pub output_path: Option<String>,
    pub encoder: String,
    pub bitrate_kbps: u32,
}

impl Default for CaptureConfig {
    fn default() -> Self {
        Self {
            fps: 60,
            queue_capacity: 3,
            output_path: None,
            encoder: "auto".to_owned(),
            bitrate_kbps: 12_000,
        }
    }
}

impl CaptureConfig {
    fn validate(mut self) -> Result<Self, CaptureError> {
        if !(1..=240).contains(&self.fps) {
            return Err(CaptureError::InvalidConfig("fps must be between 1 and 240"));
        }
        if !(1..=16).contains(&self.queue_capacity) {
            return Err(CaptureError::InvalidConfig(
                "queue_capacity must be between 1 and 16",
            ));
        }
        if let Some(path) = self.output_path.as_mut() {
            *path = path.trim().to_owned();
            if path.is_empty() {
                return Err(CaptureError::InvalidConfig("output_path is empty"));
            }
        }
        self.encoder = self.encoder.trim().to_owned();
        if self.encoder.is_empty() {
            self.encoder = "auto".to_owned();
        }
        if !(100..=200_000).contains(&self.bitrate_kbps) {
            return Err(CaptureError::InvalidConfig(
                "bitrate_kbps must be between 100 and 200000",
            ));
        }
        Ok(self)
    }
}

#[derive(Debug, Error)]
enum CaptureError {
    #[error("invalid capture config: {0}")]
    InvalidConfig(&'static str),
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default, Serialize)]
pub struct CaptureStats {
    pub abi_version: u32,
    pub running: u32,
    pub width: u32,
    pub height: u32,
    pub queue_depth: u32,
    pub frames_captured: u64,
    pub frames_consumed: u64,
    pub frames_dropped: u64,
    pub bytes_captured: u64,
    pub last_frame_unix_nanos: u64,
    pub media_time_nanos: u64,
    pub audio_frames_captured: u64,
    pub audio_chunks_dropped: u64,
}

#[derive(Debug)]
struct CapturedFrame {
    format: CapturePixelFormat,
    width: u32,
    height: u32,
    captured_at_unix_nanos: u64,
    pixels: Vec<u8>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum CapturePixelFormat {
    Bgra,
    Nv12,
}

impl CapturedFrame {
    fn pixel_bytes(&self) -> usize {
        let area = self.width as usize * self.height as usize;
        match self.format {
            CapturePixelFormat::Bgra => area * 4,
            CapturePixelFormat::Nv12 => area * 3 / 2,
        }
    }
}

#[derive(Debug)]
struct QueueState {
    frames: VecDeque<QueuedFrame>,
    bytes: usize,
    peak_bytes: usize,
    spool_bytes: usize,
    peak_spool_bytes: usize,
    peak_frames: usize,
    overflow_logged: bool,
    closed: bool,
}

#[derive(Debug)]
struct QueuedFrame {
    frame: CapturedFrame,
    spooled: Option<(Arc<FrameSpool>, u64, usize)>,
}

// A bounded disk-backed startup burst avoids making live delivery compete with
// expensive lossless compression of scrolling/dithered high-DPI frames.
#[derive(Debug)]
struct FrameSpool {
    reader: Mutex<File>, // drop the reopened handle before NamedTempFile unlinks (Windows)
    writer: Mutex<tempfile::NamedTempFile>,
    limit: u64,
}
impl FrameSpool {
    const MAX_BYTES: u64 = 2 * 1024 * 1024 * 1024;
    fn new(directory: &std::path::Path) -> std::io::Result<Self> {
        let file = tempfile::Builder::new()
            .prefix("mqol-frames-")
            .suffix(".tmp")
            .tempfile_in(directory)?;
        Ok(Self {
            reader: Mutex::new(file.reopen()?),
            writer: Mutex::new(file),
            limit: Self::MAX_BYTES,
        })
    }
    fn append(&self, bytes: &[u8]) -> std::io::Result<Option<u64>> {
        let mut file = self.writer.lock().unwrap_or_else(|p| p.into_inner());
        let offset = file.stream_position()?;
        if offset + bytes.len() as u64 > self.limit {
            return Ok(None);
        }
        file.write_all(bytes)?;
        Ok(Some(offset))
    }
    fn read(&self, offset: u64, length: usize) -> std::io::Result<Vec<u8>> {
        let mut file = self.reader.lock().unwrap_or_else(|p| p.into_inner());
        file.seek(SeekFrom::Start(offset))?;
        let mut bytes = vec![0; length];
        file.read_exact(&mut bytes)?;
        Ok(bytes)
    }
}

#[derive(Debug, Default)]
struct FramePreparation {
    #[cfg(feature = "ffmpeg")]
    preparation: Option<encoder::Nv12Preparation>,
    spool: Option<Arc<FrameSpool>>,
}

mod frame_cadence;

pub(crate) fn video_tick(timestamp: u64, origin: u64, fps: u32) -> u128 {
    // Acquisition selects frames. The encoder/editor use the same tick mapping
    // only to assign PTS, NOT to select frames a second time. Subtract AFTER
    // quantization; raw source timestamps/audio origins remain unchanged.
    let tick = |time| (u128::from(time) * u128::from(fps) + 500_000_000) / 1_000_000_000;
    tick(timestamp).saturating_sub(tick(origin))
}

#[derive(Debug)]
struct LatestFrameQueue {
    spool_directory: Option<PathBuf>,
    preparation: Mutex<FramePreparation>,
    capacity: usize,
    burst_capacity: usize,
    state: Mutex<QueueState>,
    available: Condvar,
}

#[derive(Debug)]
struct AudioChunk {
    media_time_nanos: u64,
    sample_rate: u32,
    channels: u16,
    bus_id: u16,
    samples: Vec<f32>,
}

#[derive(Debug)]
struct AudioQueueState {
    chunks: VecDeque<AudioChunk>,
    free_buffers: Vec<Vec<f32>>,
    closed: bool,
}

#[derive(Debug)]
struct AudioChunkQueue {
    state: Mutex<AudioQueueState>,
    available: Condvar,
}

#[derive(Debug)]
struct AudioBusClock {
    next_nanos: AtomicU64,
}

impl AudioBusClock {
    const RESYNC_THRESHOLD_NANOS: u64 = 2_000_000;

    const fn new() -> Self {
        Self {
            next_nanos: AtomicU64::new(u64::MAX),
        }
    }

    fn reset(&self) {
        self.next_nanos.store(u64::MAX, Ordering::Release);
    }

    fn reserve(&self, frame_count: u64, sample_rate: u32, fallback_nanos: u64) -> u64 {
        let duration_nanos = frame_count
            .saturating_mul(1_000_000_000)
            .checked_div(u64::from(sample_rate))
            .unwrap_or(0);
        let mut observed = self.next_nanos.load(Ordering::Acquire);
        loop {
            // Source timestamps derive from the FMOD sample clock, not worker arrival.
            // Preserve dropped blocks/stalls instead of compressing their missing duration.
            let start = if observed == u64::MAX
                || fallback_nanos.saturating_sub(observed) > Self::RESYNC_THRESHOLD_NANOS
            {
                fallback_nanos
            } else {
                observed
            };
            let next = start.saturating_add(duration_nanos);
            match self.next_nanos.compare_exchange_weak(
                observed,
                next,
                Ordering::AcqRel,
                Ordering::Acquire,
            ) {
                Ok(_) => return start,
                Err(actual) => observed = actual,
            }
        }
    }
}

impl AudioChunkQueue {
    fn new() -> Self {
        let free_buffers = (0..AUDIO_QUEUE_CAPACITY)
            .map(|_| Vec::with_capacity(AUDIO_MAX_SAMPLES_PER_CHUNK))
            .collect();
        Self {
            state: Mutex::new(AudioQueueState {
                chunks: VecDeque::with_capacity(AUDIO_QUEUE_CAPACITY),
                free_buffers,
                closed: false,
            }),
            available: Condvar::new(),
        }
    }

    fn try_push(
        &self,
        media_time_nanos: u64,
        sample_rate: u32,
        channels: u16,
        bus_id: u16,
        samples: &[f32],
    ) -> bool {
        if samples.len() > AUDIO_MAX_SAMPLES_PER_CHUNK {
            return false;
        }
        // This is a sink worker, NOT the FMOD mixer. A short bookkeeping lock
        // must not discard PCM. Disk writes are performed outside this lock.
        let mut state = self.state.lock().unwrap_or_else(|p| p.into_inner());
        if state.closed {
            return false;
        }
        let Some(mut buffer) = state.free_buffers.pop() else {
            return false;
        };
        buffer.clear();
        buffer.extend_from_slice(samples);
        state.chunks.push_back(AudioChunk {
            media_time_nanos,
            sample_rate,
            channels,
            bus_id,
            samples: buffer,
        });
        self.available.notify_one();
        true
    }

    fn pop(&self) -> Option<AudioChunk> {
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        loop {
            if let Some(chunk) = state.chunks.pop_front() {
                return Some(chunk);
            }
            if state.closed {
                return None;
            }
            state = self
                .available
                .wait(state)
                .unwrap_or_else(|poisoned| poisoned.into_inner());
        }
    }

    fn recycle(&self, mut chunk: AudioChunk) {
        chunk.samples.clear();
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if state.free_buffers.len() < AUDIO_QUEUE_CAPACITY {
            state.free_buffers.push(chunk.samples);
        }
    }

    fn close(&self) {
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        state.closed = true;
        self.available.notify_all();
    }
}

impl LatestFrameQueue {
    const BURST_BYTES: usize = 192 * 1024 * 1024;
    fn new(capacity: usize) -> Self {
        Self {
            spool_directory: None,
            preparation: Mutex::new(FramePreparation::default()),
            capacity,
            burst_capacity: 0,
            state: Mutex::new(QueueState {
                frames: VecDeque::with_capacity(capacity),
                bytes: 0,
                peak_bytes: 0,
                spool_bytes: 0,
                peak_spool_bytes: 0,
                peak_frames: 0,
                overflow_logged: false,
                closed: false,
            }),
            available: Condvar::new(),
        }
    }

    fn recording(capacity: usize, fps: u32) -> Self {
        Self {
            burst_capacity: capacity + fps as usize * 5,
            spool_directory: Some(std::env::temp_dir()),
            ..Self::new(capacity)
        }
    }

    fn push_latest(&self, mut frame: CapturedFrame) -> bool {
        // Serialize preparation across producers, but never hold the queue lock
        // during conversion or IO. Renderer/mixer threads never enter this path.
        let mut preparation = self.preparation.lock().unwrap_or_else(|p| p.into_inner());
        let spill = {
            let mut state = self.state.lock().unwrap_or_else(|p| p.into_inner());
            if state.closed {
                return true;
            }
            if self.burst_capacity != 0 && state.frames.len() >= self.burst_capacity {
                if !state.overflow_logged {
                    eprintln!(
                        "[mqol-encoder] frame backlog count limit reached: {}",
                        self.burst_capacity
                    );
                    state.overflow_logged = true;
                }
                return true; // reject BEFORE writing more temporary data
            }
            self.burst_capacity != 0
                && (!state.frames.is_empty()
                    || state.bytes + frame.pixels.capacity() > Self::BURST_BYTES)
        };
        let spooled = if spill {
            // Store the representation the encoder needs, never convert it back
            // to BGRA. No content-dependent compression cost on the live worker.
            #[cfg(feature = "ffmpeg")]
            {
                frame = encoder::prepare_nv12(&mut preparation.preparation, frame)
                    .expect("BGRA to recording NV12 conversion failed");
            }
            let spool = preparation.spool.get_or_insert_with(|| {
                Arc::new(
                    FrameSpool::new(
                        self.spool_directory
                            .as_ref()
                            .expect("recording spool directory"),
                    )
                    .expect("cannot create recording frame spool"),
                )
            });
            let Some(offset) = spool
                .append(&frame.pixels)
                .expect("cannot write recording frame spool")
            else {
                let mut state = self.state.lock().unwrap_or_else(|p| p.into_inner());
                if !state.overflow_logged {
                    eprintln!("[mqol-encoder] frame spool reached its 2-GiB segment limit");
                    state.overflow_logged = true;
                }
                return true;
            };
            let stored = Some((spool.clone(), offset, frame.pixels.len()));
            frame.pixels = Vec::new();
            stored
        } else {
            // Never truncate a file an in-flight reader still owns. Its Arc
            // removes the previous segment after that read finishes.
            preparation.spool = None;
            None
        };
        let queued = QueuedFrame { frame, spooled };
        let mut state = self.state.lock().unwrap_or_else(|p| p.into_inner());
        if state.closed {
            return true;
        }
        if self.burst_capacity != 0
            && state.bytes + queued.frame.pixels.capacity() > Self::BURST_BYTES
        {
            return true;
        }
        let dropped = if self.burst_capacity == 0 && state.frames.len() == self.capacity {
            let old = state.frames.pop_front().unwrap();
            state.bytes -= old.frame.pixels.capacity();
            true
        } else {
            false
        };
        state.bytes += queued.frame.pixels.capacity();
        if let Some((_, _, length)) = &queued.spooled {
            state.spool_bytes += length;
        }
        state.peak_spool_bytes = state.peak_spool_bytes.max(state.spool_bytes);
        state.frames.push_back(queued);
        state.peak_bytes = state.peak_bytes.max(state.bytes);
        state.peak_frames = state.peak_frames.max(state.frames.len());
        self.available.notify_one();
        dropped
    }

    fn pop(&self) -> Option<CapturedFrame> {
        let mut state = self.state.lock().unwrap_or_else(|p| p.into_inner());
        loop {
            if let Some(mut queued) = state.frames.pop_front() {
                state.bytes -= queued.frame.pixels.capacity();
                if let Some((_, _, length)) = &queued.spooled {
                    state.spool_bytes -= length;
                }
                drop(state);
                if let Some((spool, offset, length)) = queued.spooled {
                    queued.frame.pixels = spool
                        .read(offset, length)
                        .expect("cannot read recording frame spool");
                }
                return Some(queued.frame);
            }
            if state.closed {
                return None;
            }
            state = self
                .available
                .wait(state)
                .unwrap_or_else(|p| p.into_inner());
        }
    }

    fn close(&self) {
        let mut state = self
            .state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        state.closed = true;
        self.available.notify_all();
    }

    fn depth(&self) -> usize {
        self.state
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .frames
            .len()
    }
}

#[derive(Debug, Default)]
struct AtomicStats {
    width: AtomicU32,
    height: AtomicU32,
    frames_captured: AtomicU64,
    frames_consumed: AtomicU64,
    frames_dropped: AtomicU64,
    bytes_captured: AtomicU64,
    last_frame_unix_nanos: AtomicU64,
    media_time_nanos: AtomicU64,
    audio_frames_captured: AtomicU64,
    audio_chunks_dropped: AtomicU64,
}

#[derive(Debug)]
struct CaptureSession {
    config: CaptureConfig,
    running: AtomicBool,
    started: AtomicBool,
    origin_nanos: AtomicU64,
    last_video_nanos: AtomicU64,
    keyframes: Mutex<VecDeque<u64>>,
    lifecycle: Mutex<()>,
    queue: Arc<LatestFrameQueue>,
    audio_queue: Arc<AudioChunkQueue>,
    audio_clocks: [AudioBusClock; AUDIO_BUS_COUNT],
    stats: Arc<AtomicStats>,

    consumer_thread: Mutex<Option<JoinHandle<()>>>,
    audio_thread: Mutex<Option<JoinHandle<()>>>,
}

impl CaptureSession {
    fn new(config: CaptureConfig) -> Self {
        let queue = if let Some(output) = &config.output_path {
            let mut queue = LatestFrameQueue::recording(config.queue_capacity, config.fps);
            // The output directory is created by the bridge before subscribing.
            queue.spool_directory = std::path::Path::new(output).parent().map(ToOwned::to_owned);
            queue
        } else {
            LatestFrameQueue::new(config.queue_capacity)
        };
        if let Some(directory) = &queue.spool_directory {
            // An unusable directory is reported by the encoder or spool writer.
            let _ = std::fs::create_dir_all(directory);
        }
        Self {
            queue: Arc::new(queue),
            audio_queue: Arc::new(AudioChunkQueue::new()),
            audio_clocks: [
                AudioBusClock::new(),
                AudioBusClock::new(),
                AudioBusClock::new(),
            ],
            config,
            running: AtomicBool::new(false),
            started: AtomicBool::new(false),
            origin_nanos: AtomicU64::new(u64::MAX),
            last_video_nanos: AtomicU64::new(0),
            keyframes: Mutex::new(VecDeque::new()),
            lifecycle: Mutex::new(()),
            stats: Arc::new(AtomicStats::default()),

            consumer_thread: Mutex::new(None),
            audio_thread: Mutex::new(None),
        }
    }

    fn start(self: &Arc<Self>) -> Result<(), i32> {
        let _lifecycle = self.lifecycle.lock().unwrap_or_else(|p| p.into_inner());
        // Closed queues cannot be restarted. Allocate a new sink for each recording.
        if self.started.swap(true, Ordering::AcqRel) {
            return Err(ERR_ALREADY_RUNNING);
        }
        #[cfg(not(feature = "ffmpeg"))]
        if self.config.output_path.is_some() {
            set_last_error("this native library was built without FFmpeg encoding support");
            return Err(ERR_PLATFORM);
        }
        self.running.store(true, Ordering::Release);
        let consumer_session = Arc::clone(self);
        let consumer = thread::Builder::new()
            .name("mqol-encoder".into())
            .spawn(move || {
                match catch_unwind(AssertUnwindSafe(|| run_consumer(&consumer_session))) {
                    Ok(Ok(())) => {}
                    Ok(Err(error)) => {
                        set_last_error(error);
                        consumer_session.fail();
                    }
                    Err(payload) => {
                        set_last_error(panic_payload_message(payload));
                        consumer_session.fail();
                    }
                }
            })
            .map_err(|error| {
                self.fail();
                set_last_error(error.to_string());
                ERR_CAPTURE
            })?;
        *self
            .consumer_thread
            .lock()
            .unwrap_or_else(|p| p.into_inner()) = Some(consumer);
        let audio_session = Arc::clone(self);
        let audio = thread::Builder::new()
            .name("mqol-audio-writer".into())
            .spawn(move || {
                match catch_unwind(AssertUnwindSafe(|| run_audio_writer(&audio_session))) {
                    Ok(Ok(())) => {}
                    Ok(Err(error)) => {
                        set_last_error(error);
                        audio_session.fail();
                    }
                    Err(payload) => {
                        set_last_error(panic_payload_message(payload));
                        audio_session.fail();
                    }
                }
            })
            .map_err(|error| {
                self.fail();
                self.join_threads();
                set_last_error(error.to_string());
                ERR_CAPTURE
            })?;
        *self.audio_thread.lock().unwrap_or_else(|p| p.into_inner()) = Some(audio);
        Ok(())
    }

    fn fail(&self) {
        self.running.store(false, Ordering::Release);
        self.queue.close();
        self.audio_queue.close();
    }

    fn stop(&self) -> Result<(), i32> {
        let _lifecycle = self.lifecycle.lock().unwrap_or_else(|p| p.into_inner());
        self.fail();
        self.join_threads();
        self.queue
            .preparation
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .spool = None;
        Ok(())
    }

    fn join_threads(&self) {
        for slot in [&self.consumer_thread, &self.audio_thread] {
            if let Some(thread) = slot.lock().unwrap_or_else(|p| p.into_inner()).take() {
                let _ = thread.join();
            }
        }
    }

    fn accepts_timestamp(&self, timestamp: u64) -> bool {
        // Source already selected this frame for this sink. Only reject stale
        // or duplicate delivery, never perform another frame-rate selection.
        self.origin_nanos.load(Ordering::Acquire) == u64::MAX
            || timestamp > self.last_video_nanos.load(Ordering::Acquire)
    }

    fn push_frame(&self, frame: CapturedFrame) {
        if !self.running.load(Ordering::Acquire) {
            return;
        }
        let timestamp = frame.captured_at_unix_nanos;
        if !self.accepts_timestamp(timestamp) {
            return;
        }
        // PR #2 audio-sync only: the first video frame establishes the audio origin.
        // Timestamp is saved at GPU submission, never at delayed readback/delivery.
        if self.origin_nanos.load(Ordering::Acquire) == u64::MAX {
            for clock in &self.audio_clocks {
                clock.reset();
            }
            self.origin_nanos.store(timestamp, Ordering::Release);
        }
        self.last_video_nanos.store(timestamp, Ordering::Release);
        self.stats.width.store(frame.width, Ordering::Relaxed);
        self.stats.height.store(frame.height, Ordering::Relaxed);
        self.stats.frames_captured.fetch_add(1, Ordering::Relaxed);
        self.stats
            .bytes_captured
            .fetch_add(frame.pixels.len() as u64, Ordering::Relaxed);
        self.stats
            .last_frame_unix_nanos
            .store(timestamp, Ordering::Relaxed);
        self.stats.media_time_nanos.store(
            timestamp.saturating_sub(self.origin_nanos.load(Ordering::Acquire)),
            Ordering::Relaxed,
        );
        if self.queue.push_latest(frame) {
            self.stats.frames_dropped.fetch_add(1, Ordering::Relaxed);
        }
    }

    fn stats(&self) -> CaptureStats {
        CaptureStats {
            abi_version: ABI_VERSION,
            running: u32::from(self.running.load(Ordering::Acquire)),
            width: self.stats.width.load(Ordering::Relaxed),
            height: self.stats.height.load(Ordering::Relaxed),
            queue_depth: self.queue.depth().try_into().unwrap_or(u32::MAX),
            frames_captured: self.stats.frames_captured.load(Ordering::Relaxed),
            frames_consumed: self.stats.frames_consumed.load(Ordering::Relaxed),
            frames_dropped: self.stats.frames_dropped.load(Ordering::Relaxed),
            bytes_captured: self.stats.bytes_captured.load(Ordering::Relaxed),
            last_frame_unix_nanos: self.stats.last_frame_unix_nanos.load(Ordering::Relaxed),
            media_time_nanos: self.stats.media_time_nanos.load(Ordering::Relaxed),
            audio_frames_captured: self.stats.audio_frames_captured.load(Ordering::Relaxed),
            audio_chunks_dropped: self.stats.audio_chunks_dropped.load(Ordering::Relaxed),
        }
    }
}

fn run_consumer(session: &Arc<CaptureSession>) -> Result<(), String> {
    #[cfg(not(feature = "ffmpeg"))]
    if session.config.output_path.is_some() {
        return Err("this native library was built without FFmpeg encoding support".to_owned());
    }
    #[cfg(feature = "ffmpeg")]
    let mut encoder = None;
    while let Some(frame) = session.queue.pop() {
        #[cfg(not(feature = "ffmpeg"))]
        let _ = &frame;
        #[cfg(feature = "ffmpeg")]
        if session.config.output_path.is_some() {
            if encoder.is_none() {
                encoder = Some(
                    encoder::VideoFileEncoder::create(&session.config, &frame)
                        .map_err(|error| error.to_string())?,
                );
            }
            encoder
                .as_mut()
                .unwrap()
                .set_origin(session.origin_nanos.load(Ordering::Acquire));
            {
                let mut requests = session.keyframes.lock().unwrap_or_else(|p| p.into_inner());
                while requests
                    .front()
                    .is_some_and(|time| *time <= frame.captured_at_unix_nanos)
                {
                    requests.pop_front();
                    encoder.as_mut().unwrap().request_keyframe();
                }
            }
            encoder
                .as_mut()
                .expect("encoder initialized above")
                .encode(&frame)
                .map_err(|error| error.to_string())?;
        }
        session
            .stats
            .frames_consumed
            .fetch_add(1, Ordering::Relaxed);
    }
    #[cfg(feature = "ffmpeg")]
    if let Some(mut encoder) = encoder {
        encoder.finish().map_err(|error| error.to_string())?;
    }
    if let Some(path) = &session.config.output_path {
        let state = session
            .queue
            .state
            .lock()
            .unwrap_or_else(|p| p.into_inner());
        eprintln!(
            "[mqol-encoder] {path}: burst peak={} frames / {} RAM bytes / {} queued spool bytes; consumed={} dropped={}",
            state.peak_frames,
            state.peak_bytes,
            state.peak_spool_bytes,
            session.stats.frames_consumed.load(Ordering::Relaxed),
            session.stats.frames_dropped.load(Ordering::Relaxed)
        );
    }
    Ok(())
}

fn run_audio_writer(session: &Arc<CaptureSession>) -> Result<(), String> {
    // Recording is event-only. Retire any legacy public PCM submissions without
    // creating an audio file; the managed recorder never submits PCM.
    while let Some(chunk) = session.audio_queue.pop() {
        session.audio_queue.recycle(chunk);
    }
    Ok(())
}

#[cfg(test)]
fn write_audio_chunk(writer: &mut impl Write, chunk: &AudioChunk) -> Result<(), String> {
    let frames = chunk.samples.len() / chunk.channels as usize;
    writer
        .write_all(&chunk.media_time_nanos.to_le_bytes())
        .and_then(|_| writer.write_all(&chunk.sample_rate.to_le_bytes()))
        .and_then(|_| writer.write_all(&chunk.channels.to_le_bytes()))
        .and_then(|_| writer.write_all(&chunk.bus_id.to_le_bytes()))
        .and_then(|_| writer.write_all(&(frames as u32).to_le_bytes()))
        .and_then(|_| writer.write_all(&(chunk.samples.len() as u32).to_le_bytes()))
        .map_err(|error| format!("cannot write audio chunk header: {error}"))?;
    // SAFETY: f32 has no padding and the slice remains alive for this write call.
    let bytes = unsafe {
        std::slice::from_raw_parts(
            chunk.samples.as_ptr().cast::<u8>(),
            chunk.samples.len() * std::mem::size_of::<f32>(),
        )
    };
    writer
        .write_all(bytes)
        .map_err(|error| format!("cannot write audio chunk samples: {error}"))
}

impl Drop for CaptureSession {
    fn drop(&mut self) {
        self.queue.close();
        self.audio_queue.close();
    }
}

fn sessions() -> &'static Mutex<HashMap<u64, Arc<CaptureSession>>> {
    SESSIONS.get_or_init(|| Mutex::new(HashMap::new()))
}

fn set_last_error(message: impl Into<String>) {
    *LAST_ERROR
        .get_or_init(|| Mutex::new(String::new()))
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner()) = message.into();
}

fn panic_payload_message(payload: Box<dyn std::any::Any + Send>) -> String {
    if let Some(message) = payload.downcast_ref::<&str>() {
        (*message).to_owned()
    } else if let Some(message) = payload.downcast_ref::<String>() {
        message.clone()
    } else {
        "non-string panic payload".to_owned()
    }
}

fn ffi_status(operation: impl FnOnce() -> Result<i32, i32>) -> i32 {
    match catch_unwind(AssertUnwindSafe(operation)) {
        Ok(Ok(status)) => status,
        Ok(Err(status)) => status,
        Err(_) => {
            set_last_error("native capture panicked");
            ERR_PANIC
        }
    }
}

unsafe fn utf8_from_raw<'a>(pointer: *const u8, length: usize) -> Result<&'a str, i32> {
    if pointer.is_null() || length == 0 {
        set_last_error("null or empty UTF-8 input");
        return Err(ERR_INVALID_ARGUMENT);
    }
    // SAFETY: The caller promises that `pointer..pointer+length` is readable for this call.
    let bytes = unsafe { std::slice::from_raw_parts(pointer, length) };
    std::str::from_utf8(bytes).map_err(|error| {
        set_last_error(format!("invalid UTF-8 input: {error}"));
        ERR_INVALID_ARGUMENT
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mqol_capture_abi_version() -> u32 {
    ABI_VERSION
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_capture_create(
    config_json: *const u8,
    config_length: usize,
    output_handle: *mut u64,
) -> i32 {
    ffi_status(|| {
        if output_handle.is_null() {
            set_last_error("output_handle is null");
            return Err(ERR_INVALID_ARGUMENT);
        }
        // SAFETY: Validated above and required by the exported ABI contract.
        let json = unsafe { utf8_from_raw(config_json, config_length)? };
        let config = serde_json::from_str::<CaptureConfig>(json)
            .map_err(|error| {
                set_last_error(format!("invalid capture config JSON: {error}"));
                ERR_INVALID_ARGUMENT
            })?
            .validate()
            .map_err(|error| {
                set_last_error(error.to_string());
                ERR_INVALID_ARGUMENT
            })?;
        let handle = NEXT_HANDLE.fetch_add(1, Ordering::Relaxed);
        sessions()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .insert(handle, Arc::new(CaptureSession::new(config)));
        // SAFETY: `output_handle` was checked for null and points to caller-owned writable memory.
        unsafe { ptr::write(output_handle, handle) };
        Ok(OK)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mqol_capture_start(handle: u64) -> i32 {
    ffi_status(|| {
        let session = sessions()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .get(&handle)
            .cloned()
            .ok_or(ERR_NOT_FOUND)?;
        session.start()?;
        Ok(OK)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mqol_capture_stop(handle: u64) -> i32 {
    ffi_status(|| {
        let session = sessions()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .get(&handle)
            .cloned()
            .ok_or(ERR_NOT_FOUND)?;
        session.stop()?;
        Ok(OK)
    })
}

/// Force the first accepted frame at/after this source-clock boundary to start
/// a new GOP. Timestamped requests cannot accidentally target an old backlog frame.
#[unsafe(no_mangle)]
pub extern "C" fn mqol_capture_request_keyframe(handle: u64, timestamp: u64) -> i32 {
    ffi_status(|| {
        let session = sessions()
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .get(&handle)
            .cloned()
            .ok_or(ERR_NOT_FOUND)?;
        let mut requests = session.keyframes.lock().unwrap_or_else(|p| p.into_inner());
        if requests
            .back()
            .is_some_and(|previous| timestamp <= *previous)
        {
            return Err(ERR_INVALID_ARGUMENT);
        }
        requests.push_back(timestamp);
        Ok(OK)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_capture_get_stats(handle: u64, output: *mut CaptureStats) -> i32 {
    ffi_status(|| {
        if output.is_null() {
            set_last_error("stats output pointer is null");
            return Err(ERR_INVALID_ARGUMENT);
        }
        let session = sessions()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .get(&handle)
            .cloned()
            .ok_or(ERR_NOT_FOUND)?;
        // SAFETY: `output` was checked for null and is writable for one CaptureStats value.
        unsafe { ptr::write(output, session.stats()) };
        Ok(OK)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mqol_capture_destroy(handle: u64) -> i32 {
    ffi_status(|| {
        let session = sessions()
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .remove(&handle)
            .ok_or(ERR_NOT_FOUND)?;
        let _ = session.stop();
        Ok(OK)
    })
}

/// Push top-down packed BGRA8 from the shared source; input is borrowed for this call.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_capture_push_frame(
    handle: u64,
    pixels: *const u8,
    length: usize,
    width: u32,
    height: u32,
    timestamp: u64,
) -> i32 {
    ffi_status(|| {
        if pixels.is_null() || sdl_readback::pixel_bytes(width, height) != Some(length) {
            return Err(ERR_INVALID_ARGUMENT);
        }
        let session = sessions()
            .lock()
            .unwrap_or_else(|p| p.into_inner())
            .get(&handle)
            .cloned()
            .ok_or(ERR_NOT_FOUND)?;
        if !session.running.load(Ordering::Acquire) || !session.accepts_timestamp(timestamp) {
            return Ok(OK);
        }
        // SAFETY: caller provides length readable bytes; dimensions and size were checked.
        let mut bgra = Vec::with_capacity(length + 64);
        bgra.extend_from_slice(unsafe { std::slice::from_raw_parts(pixels, length) });
        session.push_frame(CapturedFrame {
            format: crate::CapturePixelFormat::Bgra,
            width,
            height,
            captured_at_unix_nanos: timestamp,
            pixels: bgra,
        });
        Ok(OK)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_capture_push_audio(
    handle: u64,
    samples: *const f32,
    sample_count: usize,
    sample_rate: u32,
    channels: u16,
    bus_id: u16,
    timestamp_nanos: u64,
) -> i32 {
    ffi_status(|| {
        if samples.is_null()
            || sample_count == 0
            || !(8_000..=384_000).contains(&sample_rate)
            || !(1..=32).contains(&channels)
            || bus_id == 0
            || usize::from(bus_id) > AUDIO_BUS_COUNT
            || !sample_count.is_multiple_of(channels as usize)
        {
            set_last_error("invalid audio chunk");
            return Err(ERR_INVALID_ARGUMENT);
        }
        // Called by an isolated subscription worker, never by the mixer. The old
        // try_lock silently lost audio whenever another sink queried its stats.
        let guard = sessions().lock().unwrap_or_else(|p| p.into_inner());
        let session = guard.get(&handle).cloned().ok_or(ERR_NOT_FOUND)?;
        drop(guard);
        if session.config.output_path.is_none() || !session.running.load(Ordering::Acquire) {
            return Ok(OK);
        }
        let origin = session.origin_nanos.load(Ordering::Acquire);
        if origin == u64::MAX || timestamp_nanos < origin {
            return Ok(OK);
        }
        // SAFETY: The caller guarantees `sample_count` readable f32 samples for this call.
        let values = unsafe { std::slice::from_raw_parts(samples, sample_count) };
        let frame_count = (sample_count / channels as usize) as u64;
        let media_time_nanos = session.audio_clocks[usize::from(bus_id) - 1].reserve(
            frame_count,
            sample_rate,
            timestamp_nanos.saturating_sub(origin),
        );
        // Locked FMOD buses continue producing zero-filled blocks while idle. Advance the bus
        // clock above, but do not spend queue or disk bandwidth on silence; the finalizer fills
        // timestamp gaps explicitly.
        if !values.iter().any(|sample| *sample != 0.0) {
            return Ok(OK);
        }
        if session
            .audio_queue
            .try_push(media_time_nanos, sample_rate, channels, bus_id, values)
        {
            session
                .stats
                .audio_frames_captured
                .fetch_add(frame_count, Ordering::Relaxed);
        } else {
            session
                .stats
                .audio_chunks_dropped
                .fetch_add(1, Ordering::Relaxed);
        }
        Ok(OK)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_capture_last_error(buffer: *mut c_char, capacity: usize) -> usize {
    let message = LAST_ERROR
        .get_or_init(|| Mutex::new(String::new()))
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .clone();
    let required = message.len() + 1;
    if buffer.is_null() || capacity == 0 {
        return required;
    }
    let copied = message.len().min(capacity.saturating_sub(1));
    // SAFETY: The caller provides `capacity` writable bytes. We copy at most capacity - 1 and
    // always append a trailing NUL.
    unsafe {
        ptr::copy_nonoverlapping(message.as_ptr(), buffer.cast::<u8>(), copied);
        ptr::write(buffer.add(copied), 0);
    }
    required
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_recording_finalize(plan_json: *const u8, plan_length: usize) -> i32 {
    // SAFETY: Forward the same validated buffer to the extended entry point without a callback.
    unsafe {
        mqol_recording_finalize_with_progress(
            plan_json,
            plan_length,
            None,
            ptr::null_mut(),
            None,
            ptr::null_mut(),
        )
    }
}

type FinalizeProgressCallback = unsafe extern "C" fn(f32, *mut c_void);
type FinalizeSfxCallback =
    unsafe extern "C" fn(*const u8, usize, *const u8, usize, *mut c_void) -> i32;

#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_recording_finalize_with_progress(
    plan_json: *const u8,
    plan_length: usize,
    progress: Option<FinalizeProgressCallback>,
    progress_context: *mut c_void,
    render_sfx: Option<FinalizeSfxCallback>,
    render_sfx_context: *mut c_void,
) -> i32 {
    ffi_status(|| {
        #[cfg(feature = "ffmpeg")]
        {
            // SAFETY: The exported ABI requires a readable UTF-8 buffer for this call.
            let json = unsafe { utf8_from_raw(plan_json, plan_length)? };
            let plan = serde_json::from_str::<finalizer::FinalizePlan>(json).map_err(|error| {
                set_last_error(format!("invalid finalize plan JSON: {error}"));
                ERR_INVALID_ARGUMENT
            })?;
            finalizer::finalize_with_progress(
                &plan,
                |value| {
                    if let Some(callback) = progress {
                        // SAFETY: The caller keeps the callback and context alive for this synchronous call.
                        unsafe { callback(value, progress_context) };
                    }
                },
                render_sfx,
                render_sfx_context,
            )
            .map_err(|error| {
                set_last_error(error.to_string());
                ERR_CAPTURE
            })?;
            Ok(OK)
        }
        #[cfg(not(feature = "ffmpeg"))]
        {
            let _ = (
                plan_json,
                plan_length,
                progress,
                progress_context,
                render_sfx,
                render_sfx_context,
            );
            set_last_error("native FFmpeg finalization is unavailable in this build");
            Err(ERR_PLATFORM)
        }
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mqol_capture_reserved(_value: *mut c_void) -> i32 {
    ERR_PLATFORM
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub struct RasterResult {
    pub pixels: *mut u8,
    pub pixels_length: usize,
    pub width: u32,
    pub height: u32,
    pub texture_offset_x: f32,
    pub texture_offset_y: f32,
    pub layout_width: f32,
    pub layout_height: f32,
    pub visual_x: f32,
    pub visual_y: f32,
    pub visual_width: f32,
    pub visual_height: f32,
}

fn export_raster(image: raster::RasterImage, output: *mut RasterResult) -> Result<i32, i32> {
    if output.is_null() {
        set_last_error("raster output is null");
        return Err(ERR_INVALID_ARGUMENT);
    }
    let mut pixels = image.pixels.into_boxed_slice();
    let pixels_length = pixels.len();
    let pixels_pointer = if pixels_length == 0 {
        ptr::null_mut()
    } else {
        pixels.as_mut_ptr()
    };
    std::mem::forget(pixels);
    let result = RasterResult {
        pixels: pixels_pointer,
        pixels_length,
        width: image.width,
        height: image.height,
        texture_offset_x: image.texture_offset_x,
        texture_offset_y: image.texture_offset_y,
        layout_width: image.layout_width,
        layout_height: image.layout_height,
        visual_x: image.visual_x,
        visual_y: image.visual_y,
        visual_width: image.visual_width,
        visual_height: image.visual_height,
    };
    // SAFETY: `output` was checked for null and points to caller-owned writable memory.
    unsafe { ptr::write(output, result) };
    Ok(OK)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_raster_text(
    request_json: *const u8,
    request_length: usize,
    output: *mut RasterResult,
) -> i32 {
    ffi_status(|| {
        // SAFETY: Required by the exported ABI contract.
        let json = unsafe { utf8_from_raw(request_json, request_length)? };
        let request = serde_json::from_str::<raster::TextRasterRequest>(json).map_err(|error| {
            set_last_error(format!("invalid text raster request: {error}"));
            ERR_INVALID_ARGUMENT
        })?;
        let image = raster::rasterize_text(&request).map_err(|error| {
            set_last_error(error);
            ERR_CAPTURE
        })?;
        export_raster(image, output)
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_raster_svg(
    svg_utf8: *const u8,
    svg_length: usize,
    pixel_size: u32,
    red: u8,
    green: u8,
    blue: u8,
    output: *mut RasterResult,
) -> i32 {
    ffi_status(|| {
        // SAFETY: Required by the exported ABI contract.
        let svg = unsafe { utf8_from_raw(svg_utf8, svg_length)? };
        let image = raster::rasterize_svg(svg, pixel_size, red, green, blue).map_err(|error| {
            set_last_error(error);
            ERR_CAPTURE
        })?;
        export_raster(image, output)
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mqol_raster_font_families(output: *mut RasterResult) -> i32 {
    ffi_status(|| {
        let families = raster::font_families().map_err(|error| {
            set_last_error(error);
            ERR_CAPTURE
        })?;
        let json = serde_json::to_vec(&families).map_err(|error| {
            set_last_error(format!("cannot serialize font family list: {error}"));
            ERR_CAPTURE
        })?;
        export_raster(
            raster::RasterImage {
                pixels: json,
                width: 0,
                height: 0,
                texture_offset_x: 0.0,
                texture_offset_y: 0.0,
                layout_width: 0.0,
                layout_height: 0.0,
                visual_x: 0.0,
                visual_y: 0.0,
                visual_width: 0.0,
                visual_height: 0.0,
            },
            output,
        )
    })
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_raster_free(pixels: *mut u8, pixels_length: usize) {
    if pixels.is_null() || pixels_length == 0 {
        return;
    }
    // SAFETY: The pointer and length must come from a successful raster export and are consumed once.
    unsafe {
        drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(
            pixels,
            pixels_length,
        )))
    };
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame(id: u8) -> CapturedFrame {
        CapturedFrame {
            format: crate::CapturePixelFormat::Bgra,
            width: 1,
            height: 1,
            captured_at_unix_nanos: id as u64,
            pixels: vec![id; 4],
        }
    }

    #[test]
    fn bounded_queue_drops_oldest_frame() {
        let queue = LatestFrameQueue::new(2);
        assert!(!queue.push_latest(frame(1)));
        assert!(!queue.push_latest(frame(2)));
        assert!(queue.push_latest(frame(3)));
        assert_eq!(queue.pop().unwrap().captured_at_unix_nanos, 2);
        assert_eq!(queue.pop().unwrap().captured_at_unix_nanos, 3);
    }

    #[test]
    fn config_rejects_unbounded_memory_settings() {
        assert!(
            CaptureConfig {
                queue_capacity: 0,
                ..CaptureConfig::default()
            }
            .validate()
            .is_err()
        );
        assert!(
            CaptureConfig {
                queue_capacity: 17,
                ..CaptureConfig::default()
            }
            .validate()
            .is_err()
        );
    }

    #[test]
    fn audio_queue_is_bounded_and_reuses_buffers() {
        let queue = AudioChunkQueue::new();
        let samples = [0.25_f32, -0.25];
        for index in 0..AUDIO_QUEUE_CAPACITY {
            assert!(queue.try_push(index as u64, 48_000, 2, 1, &samples));
        }
        assert!(!queue.try_push(999, 48_000, 2, 1, &samples));

        let chunk = queue.pop().unwrap();
        assert_eq!(chunk.samples, samples);
        queue.recycle(chunk);
        assert!(queue.try_push(1_000, 48_000, 2, 1, &samples));
        queue.close();
    }

    #[test]
    #[ignore = "hardware throughput: set MQOL_TEST_BGRA, MQOL_TEST_OUTPUT; run with --release"]
    #[cfg(feature = "ffmpeg")]
    fn full_resolution_recording_recovers_after_encoder_startup() {
        use std::time::{Duration, Instant};
        let pixels = std::fs::read(std::env::var("MQOL_TEST_BGRA").unwrap()).unwrap();
        let output = std::env::var("MQOL_TEST_OUTPUT").unwrap();
        let width = 2560;
        let height = 1506;
        assert_eq!(pixels.len(), width as usize * height as usize * 4);
        let session = Arc::new(CaptureSession::new(CaptureConfig {
            fps: 60,
            encoder: "auto".into(),
            output_path: Some(output),
            bitrate_kbps: 12000,
            ..Default::default()
        }));
        session.start().unwrap();
        let started = Instant::now();
        let mut push_time = Duration::ZERO;
        let frames = 1200u64;
        for i in 0..frames {
            let deadline = started + Duration::from_nanos(i * 1_000_000_000 / 60);
            if let Some(wait) = deadline.checked_duration_since(Instant::now()) {
                thread::sleep(wait);
            }
            let mut bgra = pixels.clone();
            // A visible frame id avoids using identical encoded pictures as a success criterion.
            for bit in 0..16 {
                for x in 0..32 {
                    for y in 0..32 {
                        let offset = (y * width as usize + bit * 32 + x) * 4;
                        bgra[offset..offset + 3].fill(if (i >> bit) & 1 == 0 { 0 } else { 255 });
                    }
                }
            }
            let before = Instant::now();
            session.push_frame(CapturedFrame {
                format: crate::CapturePixelFormat::Bgra,
                width,
                height,
                pixels: bgra,
                captured_at_unix_nanos: 1_000_000_000 + i * 1_000_000_000 / 60,
            });
            push_time += before.elapsed();
            if i % 120 == 119 {
                eprintln!(
                    "cadence t={:.2}s push_ms={:.2} stats={:?}",
                    started.elapsed().as_secs_f64(),
                    push_time.as_secs_f64() * 1000.0 / (i + 1) as f64,
                    session.stats()
                );
            }
        }
        let elapsed = started.elapsed();
        let depth = session.stats().queue_depth;
        session.stop().unwrap();
        let stats = session.stats();
        eprintln!(
            "cadence final wall={elapsed:?} drain={:?} {stats:?}",
            started.elapsed() - elapsed
        );
        assert!(
            elapsed.as_secs_f64() < 21.0,
            "producer could not sustain real time"
        );
        assert_eq!(stats.frames_dropped, 0);
        assert_eq!(stats.frames_consumed, frames);
        assert!(depth < 60, "encoder never recovered its startup backlog");
    }

    #[test]
    fn recording_burst_is_lossless_ordered_and_bounded() {
        let mut queue = LatestFrameQueue::recording(1, 1);
        // One raw slot plus five disk-backed burst slots.
        for i in 0..6u64 {
            let mut value = frame(i as u8);
            value.captured_at_unix_nanos = i;
            assert!(!queue.push_latest(value));
        }
        assert!(queue.push_latest(frame(7)));
        assert_eq!(queue.depth(), 6);
        for i in 0..6u64 {
            let value = queue.pop().unwrap();
            assert_eq!(value.captured_at_unix_nanos, i);
            assert_eq!(value.pixels, frame(i as u8).pixels);
        }
        assert_eq!(queue.state.lock().unwrap().bytes, 0);
        // A full RAM budget spills to disk instead of retaining another payload.
        queue.state.get_mut().unwrap().bytes = LatestFrameQueue::BURST_BYTES;
        assert!(!queue.push_latest(frame(8)));
        assert_eq!(
            queue.state.lock().unwrap().bytes,
            LatestFrameQueue::BURST_BYTES
        );
        assert_eq!(queue.pop().unwrap().pixels, frame(8).pixels);
        queue.close();
    }

    #[test]
    fn audio_bookkeeping_contention_does_not_discard_pcm() {
        let queue = Arc::new(AudioChunkQueue::new());
        let guard = queue.state.lock().unwrap();
        let other = Arc::clone(&queue);
        let worker = thread::spawn(move || other.try_push(0, 48000, 2, 1, &[0.25, -0.25]));
        thread::sleep(std::time::Duration::from_millis(30));
        drop(guard);
        assert!(worker.join().unwrap());
        assert_eq!(queue.pop().unwrap().samples, [0.25, -0.25]);
        queue.close();
    }

    #[test]
    fn audio_bus_clock_advances_and_resynchronizes_after_a_video_clock_jump() {
        let clock = AudioBusClock::new();
        assert_eq!(clock.reserve(480, 48_000, 2_000_000_000), 2_000_000_000);
        assert_eq!(clock.reserve(960, 48_000, 2_050_000_000), 2_050_000_000);
        assert_eq!(clock.reserve(480, 48_000, 9_000_000_000), 9_000_000_000);
        assert_eq!(clock.reserve(480, 48_000, 9_000_000_000), 9_010_000_000);
    }

    #[test]
    fn audio_clock_reset_and_dropped_blocks_preserve_time() {
        let clock = AudioBusClock::new();
        assert_eq!(clock.reserve(480, 48000, 0), 0);
        assert_eq!(clock.reserve(480, 48000, 30_000_000), 30_000_000);
        clock.reset();
        assert_eq!(clock.reserve(480, 48000, 0), 0);
    }

    #[test]
    fn audio_is_gated_by_first_video_and_keeps_submission_timestamp() {
        let session = Arc::new(CaptureSession::new(CaptureConfig {
            output_path: Some("unused".into()),
            ..Default::default()
        }));
        session.running.store(true, Ordering::Release);
        let handle = NEXT_HANDLE.fetch_add(1, Ordering::Relaxed);
        sessions()
            .lock()
            .unwrap()
            .insert(handle, Arc::clone(&session));
        let samples = [0.5; 960];
        let send = |timestamp| unsafe {
            mqol_capture_push_audio(
                handle,
                samples.as_ptr(),
                samples.len(),
                48000,
                2,
                1,
                timestamp,
            )
        };
        assert_eq!(send(100), OK);
        assert_eq!(session.stats().audio_frames_captured, 0);
        let mut first = frame(1);
        first.captured_at_unix_nanos = 1_000_000_000;
        session.push_frame(first);
        assert_eq!(session.origin_nanos.load(Ordering::Acquire), 1_000_000_000);
        assert_eq!(send(999_999_999), OK);
        assert_eq!(session.stats().audio_frames_captured, 0);
        assert_eq!(send(1_030_000_000), OK);
        assert_eq!(
            session.audio_queue.pop().unwrap().media_time_nanos,
            30_000_000
        );
        session.fail();
        sessions().lock().unwrap().remove(&handle);
    }

    #[test]
    fn recording_sink_never_resamples_source_selected_frames() {
        for fps in [30, 60, 120] {
            let session = CaptureSession::new(CaptureConfig {
                fps,
                ..Default::default()
            });
            session.running.store(true, Ordering::Release);
            for n in 0..144u64 {
                let mut input = frame(1);
                input.captured_at_unix_nanos = 1_000_000_000 + n * 1_000_000_000 / 144;
                session.push_frame(input);
            }
            assert_eq!(
                session.stats().frames_captured,
                144,
                "the recording sink must not select frames based on fps={fps}"
            );
        }
    }

    #[test]
    fn matching_source_fps_keeps_frames_despite_submillisecond_jitter() {
        let session = CaptureSession::new(CaptureConfig {
            fps: 60,
            ..Default::default()
        });
        session.running.store(true, Ordering::Release);
        for n in 0..1200u64 {
            let mut input = frame(1);
            let jitter = if n % 2 == 0 { 100_000 } else { 0 };
            input.captured_at_unix_nanos = 1_000_000_000 + n * 1_000_000_000 / 60 + jitter;
            session.push_frame(input);
        }
        assert_eq!(session.stats().frames_captured, 1200);
    }

    #[test]
    fn stopping_one_sink_leaves_other_running_and_restart_is_rejected() {
        let a = Arc::new(CaptureSession::new(CaptureConfig::default()));
        let b = Arc::new(CaptureSession::new(CaptureConfig::default()));
        a.start().unwrap();
        b.start().unwrap();
        a.stop().unwrap();
        b.push_frame(frame(1));
        assert!(b.running.load(Ordering::Acquire));
        assert_eq!(b.stats().frames_captured, 1);
        assert_eq!(a.start(), Err(ERR_ALREADY_RUNNING));
        b.stop().unwrap();
        b.stop().unwrap();
    }

    #[test]
    fn audio_sidecar_chunk_has_stable_little_endian_layout() {
        let chunk = AudioChunk {
            media_time_nanos: 123,
            sample_rate: 48_000,
            channels: 2,
            bus_id: 1,
            samples: vec![0.5, -0.25, 1.0, -1.0],
        };
        let mut bytes = Vec::new();
        write_audio_chunk(&mut bytes, &chunk).unwrap();

        assert_eq!(bytes.len(), 24 + 4 * size_of::<f32>());
        assert_eq!(u64::from_le_bytes(bytes[0..8].try_into().unwrap()), 123);
        assert_eq!(u32::from_le_bytes(bytes[8..12].try_into().unwrap()), 48_000);
        assert_eq!(u16::from_le_bytes(bytes[12..14].try_into().unwrap()), 2);
        assert_eq!(u16::from_le_bytes(bytes[14..16].try_into().unwrap()), 1);
        assert_eq!(u32::from_le_bytes(bytes[16..20].try_into().unwrap()), 2);
        assert_eq!(u32::from_le_bytes(bytes[20..24].try_into().unwrap()), 4);
        assert_eq!(f32::from_le_bytes(bytes[24..28].try_into().unwrap()), 0.5);
    }

    #[test]
    fn ffi_create_and_destroy_roundtrip() {
        let json = br#"{"window_title":"Celeste","fps":60,"queue_capacity":3}"#;
        let mut handle = 0;
        // SAFETY: The test provides valid pointers and lengths.
        assert_eq!(
            unsafe { mqol_capture_create(json.as_ptr(), json.len(), &mut handle) },
            OK
        );
        assert_ne!(handle, 0);
        let mut stats = CaptureStats::default();
        // SAFETY: The output pointer is valid for one CaptureStats.
        assert_eq!(unsafe { mqol_capture_get_stats(handle, &mut stats) }, OK);
        assert_eq!(stats.abi_version, ABI_VERSION);
        assert_eq!(mqol_capture_destroy(handle), OK);
    }
}

#[cfg(test)]
mod backlog_tests {
    use super::*;

    #[test]
    fn frame_spool_bounds_preserves_offsets_and_cleans_up_after_last_owner() {
        let dir = tempfile::tempdir().unwrap();
        let mut spool = FrameSpool::new(dir.path()).unwrap();
        spool.limit = 8;
        assert_eq!(spool.append(&[1, 2, 3, 4]).unwrap(), Some(0));
        assert_eq!(spool.append(&[5, 6, 7, 8]).unwrap(), Some(4));
        assert_eq!(spool.append(&[9]).unwrap(), None);
        assert_eq!(spool.read(4, 4).unwrap(), [5, 6, 7, 8]);
        assert_eq!(spool.read(0, 4).unwrap(), [1, 2, 3, 4]);
        let owner = Arc::new(spool);
        let reader = owner.clone();
        drop(owner);
        assert_eq!(std::fs::read_dir(dir.path()).unwrap().count(), 1);
        assert_eq!(reader.read(0, 8).unwrap(), [1, 2, 3, 4, 5, 6, 7, 8]);
        drop(reader);
        assert_eq!(std::fs::read_dir(dir.path()).unwrap().count(), 0);
    }
    #[test]
    fn backlog_spooling_survives_rejected_frames_raw_gaps_and_resize() {
        let mut queue = LatestFrameQueue::recording(3, 60);
        queue.burst_capacity = 3;
        let make = |id: u8, width: u32| CapturedFrame {
            format: CapturePixelFormat::Nv12,
            width,
            height: 8,
            captured_at_unix_nanos: id as u64,
            pixels: vec![id; width as usize * 8 * 3 / 2],
        };
        let check = |id: u8, width: u32, value: CapturedFrame| {
            assert_eq!(value.captured_at_unix_nanos, id as u64);
            assert_eq!(value.width, width);
            assert_eq!(value.pixels, make(id, width).pixels);
        };
        for id in 1..=3 {
            assert!(!queue.push_latest(make(id, 16)));
        }
        assert!(queue.push_latest(make(4, 16))); // rejected before writing
        check(1, 16, queue.pop().unwrap());
        assert!(!queue.push_latest(make(5, 16)));
        for id in [2, 3, 5] {
            check(id, 16, queue.pop().unwrap());
        }
        assert!(!queue.push_latest(make(6, 16))); // start a fresh burst
        assert!(!queue.push_latest(make(7, 16)));
        assert!(!queue.push_latest(make(8, 32))); // resized frame
        for (id, width) in [(6, 16), (7, 16), (8, 32)] {
            check(id, width, queue.pop().unwrap());
        }
        queue.close();
        assert!(queue.pop().is_none());
    }
}
