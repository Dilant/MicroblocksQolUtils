use std::collections::HashSet;
use std::fs;
use std::path::{Path, PathBuf};

use ffmpeg::{Dictionary, Packet, Rational, codec, encoder, format, frame, software};
use ffmpeg_next as ffmpeg;
use thiserror::Error;

use crate::{CaptureConfig, CapturePixelFormat, CapturedFrame};

#[derive(Debug, Error)]
pub enum EncoderError {
    #[error("FFmpeg initialization failed: {0}")]
    Initialize(ffmpeg::Error),
    #[error("output path has no parent directory: {0}")]
    MissingParent(PathBuf),
    #[error("cannot create output directory {path}: {source}")]
    CreateDirectory {
        path: PathBuf,
        source: std::io::Error,
    },
    #[error("no usable video encoder was found; tried {0}")]
    NoEncoder(String),
    #[error("cannot create FFmpeg output {path}: {source}")]
    CreateOutput {
        path: PathBuf,
        source: ffmpeg::Error,
    },
    #[error("cannot add the {encoder} video stream: {source}")]
    AddStream {
        encoder: String,
        source: ffmpeg::Error,
    },
    #[error("cannot open the {encoder} video encoder: {source}")]
    OpenEncoder {
        encoder: String,
        source: ffmpeg::Error,
    },
    #[error("cannot write the output header: {0}")]
    Header(ffmpeg::Error),
    #[error("cannot initialize BGRA conversion: {0}")]
    Scale(ffmpeg::Error),
    #[error("invalid BGRA frame buffer: got {actual} bytes for {width}x{height}")]
    InvalidFrame {
        actual: usize,
        width: u32,
        height: u32,
    },
    #[error("frame conversion failed: {0}")]
    Convert(ffmpeg::Error),
    #[error("video encoder rejected a frame: {0}")]
    SendFrame(ffmpeg::Error),
    #[error("cannot write an encoded packet: {0}")]
    WritePacket(ffmpeg::Error),
    #[error("cannot flush the video encoder: {0}")]
    Flush(ffmpeg::Error),
    #[error("cannot write the output trailer: {0}")]
    Trailer(ffmpeg::Error),
}

pub struct VideoFileEncoder {
    output: format::context::Output,
    encoder: encoder::video::Encoder,
    scaler: software::scaling::Context,
    input: frame::Video,
    converted: frame::Video,
    output_width: u32,
    output_height: u32,
    input_width: u32,
    input_height: u32,
    input_format: ffmpeg::format::Pixel,
    pixel_format: ffmpeg::format::Pixel,
    stream_index: usize,
    encoder_time_base: Rational,
    stream_time_base: Rational,
    fps: u32,
    origin_unix_nanos: Option<u64>,
    last_pts: i64,
    finished: bool,
    keyframe_requested: bool,
}

impl VideoFileEncoder {
    pub fn create(config: &CaptureConfig, first: &CapturedFrame) -> Result<Self, EncoderError> {
        ffmpeg::init().map_err(EncoderError::Initialize)?;
        let path = Path::new(
            config
                .output_path
                .as_deref()
                .expect("VideoFileEncoder requires an output path"),
        );
        let parent = path
            .parent()
            .filter(|parent| !parent.as_os_str().is_empty())
            .ok_or_else(|| EncoderError::MissingParent(path.to_owned()))?;
        fs::create_dir_all(parent).map_err(|source| EncoderError::CreateDirectory {
            path: parent.to_owned(),
            source,
        })?;
        let _ = fs::remove_file(path);

        let output_width = even_dimension(first.width);
        let output_height = even_dimension(first.height);
        let mut failures = Vec::new();
        for name in encoder_candidates(&config.encoder) {
            let started = std::time::Instant::now();
            match Self::try_create(
                path,
                name,
                output_width,
                output_height,
                first.width,
                first.height,
                input_format(first),
                config.fps,
                config.bitrate_kbps,
            ) {
                Ok(encoder) => {
                    eprintln!(
                        "[mqol-encoder] {}: selected {name}, open={}ms, {}x{}@{}",
                        path.display(),
                        started.elapsed().as_millis(),
                        output_width,
                        output_height,
                        config.fps
                    );
                    return Ok(encoder);
                }
                Err(error) => {
                    eprintln!(
                        "[mqol-encoder] {name}: failed after {}ms: {error}",
                        started.elapsed().as_millis()
                    );
                    failures.push(format!("{name}: {error}"));
                }
            }
        }
        Err(EncoderError::NoEncoder(failures.join("; ")))
    }

    #[allow(clippy::too_many_arguments)]
    fn try_create(
        path: &Path,
        encoder_name: &str,
        output_width: u32,
        output_height: u32,
        input_width: u32,
        input_height: u32,
        input_format: ffmpeg::format::Pixel,
        fps: u32,
        bitrate_kbps: u32,
    ) -> Result<Self, EncoderError> {
        let codec = encoder::find_by_name(encoder_name).ok_or_else(|| {
            EncoderError::NoEncoder(format!("{encoder_name} is absent from this FFmpeg build"))
        })?;
        let pixel_format = pixel_format_for_encoder(encoder_name);
        let encoder_time_base = Rational(1, fps as i32);
        let mut output = format::output(path).map_err(|source| EncoderError::CreateOutput {
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
            .map_err(|source| EncoderError::OpenEncoder {
                encoder: encoder_name.to_owned(),
                source,
            })?;
        video.set_width(output_width);
        video.set_height(output_height);
        video.set_format(pixel_format);
        video.set_time_base(encoder_time_base);
        video.set_frame_rate(Some(Rational(fps as i32, 1)));
        video.set_bit_rate(bitrate_kbps as usize * 1_000);
        video.set_gop(fps.saturating_mul(2));
        video.set_max_b_frames(0);
        if global_header {
            video.set_flags(codec::Flags::GLOBAL_HEADER);
        }
        let opened = video
            .open_as_with(codec, encoder_options(encoder_name))
            .map_err(|source| EncoderError::OpenEncoder {
                encoder: encoder_name.to_owned(),
                source,
            })?;

        let stream_index;
        {
            let mut stream =
                output
                    .add_stream(codec)
                    .map_err(|source| EncoderError::AddStream {
                        encoder: encoder_name.to_owned(),
                        source,
                    })?;
            stream.set_time_base(encoder_time_base);
            stream.set_avg_frame_rate(Rational(fps as i32, 1));
            stream.set_parameters(&opened);
            stream_index = stream.index();
        }
        output.write_header().map_err(EncoderError::Header)?;
        let stream_time_base = output
            .stream(stream_index)
            .expect("newly added FFmpeg stream disappeared")
            .time_base();
        let scaler = software::scaling::Context::get(
            input_format,
            input_width,
            input_height,
            pixel_format,
            output_width,
            output_height,
            software::scaling::Flags::BILINEAR,
        )
        .map_err(EncoderError::Scale)?;

        Ok(Self {
            output,
            encoder: opened,
            scaler,
            input: frame::Video::new(input_format, input_width, input_height),
            converted: frame::Video::new(pixel_format, output_width, output_height),
            output_width,
            output_height,
            input_width,
            input_height,
            input_format,
            pixel_format,
            stream_index,
            encoder_time_base,
            stream_time_base,
            fps,
            origin_unix_nanos: None,
            last_pts: -1,
            finished: false,
            keyframe_requested: false,
        })
    }

    pub fn set_origin(&mut self, timestamp: u64) {
        if self.origin_unix_nanos.is_none() {
            self.origin_unix_nanos = Some(timestamp);
        }
    }

    pub fn encode(&mut self, captured: &CapturedFrame) -> Result<(), EncoderError> {
        let origin = *self
            .origin_unix_nanos
            .get_or_insert(captured.captured_at_unix_nanos);
        let timestamp = crate::video_tick(captured.captured_at_unix_nanos, origin, self.fps)
            .min(i64::MAX as u128) as i64;
        if timestamp <= self.last_pts {
            return Ok(());
        }

        let input_format = input_format(captured);
        if captured.width != self.input_width
            || captured.height != self.input_height
            || input_format != self.input_format
        {
            self.input = frame::Video::new(input_format, captured.width, captured.height);
            self.input_width = captured.width;
            self.input_height = captured.height;
            self.input_format = input_format;
            self.scaler.cached(
                input_format,
                captured.width,
                captured.height,
                self.pixel_format,
                self.output_width,
                self.output_height,
                software::scaling::Flags::BILINEAR,
            );
        }

        // An asynchronous encoder may still reference the previous converted
        // frame. Never overwrite its storage while the codec owns a reference.
        let writable = unsafe { ffmpeg::ffi::av_frame_make_writable(self.converted.as_mut_ptr()) };
        if writable < 0 {
            return Err(EncoderError::Convert(ffmpeg::Error::from(writable)));
        }
        if input_format == self.pixel_format
            && captured.width == self.output_width
            && captured.height == self.output_height
        {
            copy_pixels(captured, &mut self.converted)?;
        } else {
            copy_pixels(captured, &mut self.input)?;
            self.scaler
                .run(&self.input, &mut self.converted)
                .map_err(EncoderError::Convert)?;
        }

        self.last_pts = timestamp;
        self.converted.set_pts(Some(timestamp));
        self.converted.set_kind(if self.keyframe_requested {
            ffmpeg::picture::Type::I
        } else {
            ffmpeg::picture::Type::None
        });
        self.keyframe_requested = false;
        self.encoder
            .send_frame(&self.converted)
            .map_err(EncoderError::SendFrame)?;
        self.write_available_packets()
    }

    pub fn finish(&mut self) -> Result<(), EncoderError> {
        if self.finished {
            return Ok(());
        }
        self.encoder.send_eof().map_err(EncoderError::Flush)?;
        self.write_available_packets()?;
        self.output.write_trailer().map_err(EncoderError::Trailer)?;
        self.finished = true;
        Ok(())
    }

    pub fn request_keyframe(&mut self) {
        self.keyframe_requested = true;
    }

    fn write_available_packets(&mut self) -> Result<(), EncoderError> {
        let mut packet = Packet::empty();
        while self.encoder.receive_packet(&mut packet).is_ok() {
            packet.set_stream(self.stream_index);
            packet.set_position(-1);
            packet.rescale_ts(self.encoder_time_base, self.stream_time_base);
            packet
                .write_interleaved(&mut self.output)
                .map_err(EncoderError::WritePacket)?;
        }
        Ok(())
    }
}

impl Drop for VideoFileEncoder {
    fn drop(&mut self) {
        let _ = self.finish();
    }
}

fn input_format(captured: &CapturedFrame) -> ffmpeg::format::Pixel {
    match captured.format {
        CapturePixelFormat::Bgra => ffmpeg::format::Pixel::BGRA,
        CapturePixelFormat::Nv12 => ffmpeg::format::Pixel::NV12,
    }
}

fn copy_pixels(captured: &CapturedFrame, output: &mut frame::Video) -> Result<(), EncoderError> {
    if captured.format == CapturePixelFormat::Nv12 {
        if captured.pixels.len() != captured.pixel_bytes() {
            return Err(EncoderError::InvalidFrame {
                actual: captured.pixels.len(),
                width: captured.width,
                height: captured.height,
            });
        }
        let width = captured.width as usize;
        let height = captured.height as usize;
        for (plane, rows, offset) in [(0, height, 0), (1, height / 2, width * height)] {
            let stride = output.stride(plane);
            let destination = output.data_mut(plane);
            for row in 0..rows {
                destination[row * stride..row * stride + width].copy_from_slice(
                    &captured.pixels[offset + row * width..offset + (row + 1) * width],
                );
            }
        }
        return Ok(());
    }
    let row_bytes = captured.width as usize * 4;
    let height = captured.height as usize;
    let expected = row_bytes.saturating_mul(height);
    if captured.pixels.len() < expected || height == 0 || captured.pixels.len() % height != 0 {
        return Err(EncoderError::InvalidFrame {
            actual: captured.pixels.len(),
            width: captured.width,
            height: captured.height,
        });
    }
    let input_stride = captured.pixels.len() / height;
    if input_stride < row_bytes {
        return Err(EncoderError::InvalidFrame {
            actual: captured.pixels.len(),
            width: captured.width,
            height: captured.height,
        });
    }
    let output_stride = output.stride(0);
    let data = output.data_mut(0);
    for row in 0..height {
        let source = &captured.pixels[row * input_stride..row * input_stride + row_bytes];
        let destination = &mut data[row * output_stride..row * output_stride + row_bytes];
        destination.copy_from_slice(source);
    }
    Ok(())
}

pub(crate) struct Nv12Preparation {
    width: u32,
    height: u32,
    scaler: software::scaling::Context,
}
// SwsContext has no thread affinity. The per-sink Mutex serializes all access;
// unlike TLS this does not retain full-frame buffers on many .NET pool threads.
unsafe impl Send for Nv12Preparation {}
impl std::fmt::Debug for Nv12Preparation {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Nv12Preparation")
            .field("width", &self.width)
            .field("height", &self.height)
            .finish_non_exhaustive()
    }
}

// Only the recording backlog uses this format. Its chroma conversion is the same
// conversion the video encoder already requires; never round-trip it through RGB.
pub(crate) fn prepare_nv12(
    cached: &mut Option<Nv12Preparation>,
    mut frame: CapturedFrame,
) -> Result<CapturedFrame, EncoderError> {
    if frame.format == CapturePixelFormat::Nv12 || frame.width < 2 || frame.height < 2 {
        return Ok(frame);
    }
    let w = even_dimension(frame.width);
    let h = even_dimension(frame.height);
    if cached
        .as_ref()
        .is_none_or(|c| c.width != frame.width || c.height != frame.height)
    {
        *cached = Some(Nv12Preparation {
            width: frame.width,
            height: frame.height,
            scaler: software::scaling::Context::get(
                ffmpeg::format::Pixel::BGRA,
                frame.width,
                frame.height,
                ffmpeg::format::Pixel::NV12,
                w,
                h,
                software::scaling::Flags::BILINEAR,
            )
            .map_err(EncoderError::Scale)?,
        });
    }
    let c = cached.as_mut().unwrap();
    let expected = frame.width as usize * frame.height as usize * 4;
    if frame.pixels.len() != expected {
        return Err(EncoderError::InvalidFrame {
            actual: frame.pixels.len(),
            width: frame.width,
            height: frame.height,
        });
    }
    // Convert directly between the owned queue buffers, not via two additional
    // full-size AVFrame copies. Initialized padding permits SIMD overreads at
    // the final row; the C ABI producer reserves it before its first copy.
    frame.pixels.resize(expected + 64, 0);
    let size = w as usize * h as usize * 3 / 2;
    let mut pixels = vec![0; size + 64];
    let input = [
        frame.pixels.as_ptr(),
        std::ptr::null(),
        std::ptr::null(),
        std::ptr::null(),
    ];
    let input_stride = [frame.width as i32 * 4, 0, 0, 0];
    let output = [
        pixels.as_mut_ptr(),
        unsafe { pixels.as_mut_ptr().add(w as usize * h as usize) },
        std::ptr::null_mut(),
        std::ptr::null_mut(),
    ];
    let output_stride = [w as i32, w as i32, 0, 0];
    // SAFETY: both planes and padded BGRA storage cover all rows/strides;
    // the per-sink mutex exclusively owns the matching scaler for this call.
    let rows = unsafe {
        ffmpeg::ffi::sws_scale(
            c.scaler.as_mut_ptr(),
            input.as_ptr(),
            input_stride.as_ptr(),
            0,
            frame.height as i32,
            output.as_ptr(),
            output_stride.as_ptr(),
        )
    };
    if rows != h as i32 {
        return Err(EncoderError::Convert(ffmpeg::Error::InvalidData));
    }
    pixels.truncate(size);
    Ok(CapturedFrame {
        width: w,
        height: h,
        captured_at_unix_nanos: frame.captured_at_unix_nanos,
        pixels,
        format: CapturePixelFormat::Nv12,
    })
}

pub(crate) fn even_dimension(value: u32) -> u32 {
    value.max(2) & !1
}

pub(crate) fn encoder_candidates(preferred: &str) -> Vec<&str> {
    #[cfg(windows)]
    const AUTOMATIC: [&str; 5] = [
        "h264_nvenc",
        "h264_qsv",
        "h264_amf",
        "h264_mf",
        "libopenh264",
    ];
    #[cfg(target_os = "macos")]
    const AUTOMATIC: [&str; 2] = ["h264_videotoolbox", "mpeg4"];
    #[cfg(target_os = "linux")]
    const AUTOMATIC: [&str; 3] = ["h264_nvenc", "h264_qsv", "mpeg4"];
    #[cfg(not(any(windows, target_os = "macos", target_os = "linux")))]
    const AUTOMATIC: [&str; 1] = ["mpeg4"];
    let mut result = Vec::with_capacity(AUTOMATIC.len() + 1);
    let mut seen = HashSet::new();
    let preferred = canonical_encoder_name(preferred.trim());
    if !preferred.is_empty() && !preferred.eq_ignore_ascii_case("auto") {
        result.push(preferred);
        seen.insert(preferred);
    }
    for candidate in AUTOMATIC {
        if seen.insert(candidate) {
            result.push(candidate);
        }
    }
    result
}

fn canonical_encoder_name(name: &str) -> &str {
    if name.eq_ignore_ascii_case("nvenc") {
        "h264_nvenc"
    } else if name.eq_ignore_ascii_case("qsv") {
        "h264_qsv"
    } else if name.eq_ignore_ascii_case("amf") {
        "h264_amf"
    } else if name.eq_ignore_ascii_case("mf") {
        "h264_mf"
    } else if name.eq_ignore_ascii_case("videotoolbox") {
        "h264_videotoolbox"
    } else if name.eq_ignore_ascii_case("v4l2m2m") {
        "h264_v4l2m2m"
    } else if name.eq_ignore_ascii_case("openh264") {
        "libopenh264"
    } else {
        name
    }
}

pub(crate) fn pixel_format_for_encoder(name: &str) -> ffmpeg::format::Pixel {
    if name.eq_ignore_ascii_case("libopenh264") || name.eq_ignore_ascii_case("mpeg4") {
        ffmpeg::format::Pixel::YUV420P
    } else {
        ffmpeg::format::Pixel::NV12
    }
}

pub(crate) fn encoder_options(name: &str) -> Dictionary<'static> {
    let mut options = Dictionary::new();
    match name {
        "h264_nvenc" => {
            options.set("forced-idr", "1");
            options.set("preset", "p4");
            options.set("tune", "ll");
        }
        "h264_qsv" => {
            options.set("forced_idr", "1");
            options.set("preset", "veryfast");
            // Recording can tolerate a few frames of encoder latency. Serializing
            // every QSV submission needlessly limits throughput at native DPI sizes.
            options.set("async_depth", "4");
        }
        "h264_amf" => {
            options.set("usage", "lowlatency");
            options.set("quality", "balanced");
        }
        "libopenh264" => {
            options.set("profile", "high");
        }
        _ => {}
    }
    options
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{SystemTime, UNIX_EPOCH};

    #[test]
    fn automatic_encoder_order_honors_preference_without_duplicates() {
        #[cfg(windows)]
        assert_eq!(encoder_candidates("auto")[0], "h264_nvenc");
        #[cfg(target_os = "macos")]
        assert_eq!(encoder_candidates("auto")[0], "h264_videotoolbox");
        #[cfg(target_os = "linux")]
        assert_eq!(
            encoder_candidates("auto"),
            ["h264_nvenc", "h264_qsv", "mpeg4"]
        );
        let candidates = encoder_candidates("h264_mf");
        assert_eq!(candidates[0], "h264_mf");
        assert_eq!(
            candidates
                .iter()
                .filter(|value| **value == "h264_mf")
                .count(),
            1
        );
        assert_eq!(encoder_candidates("nvenc")[0], "h264_nvenc");
        assert_eq!(encoder_candidates("QSV")[0], "h264_qsv");
    }

    #[test]
    fn backlog_nv12_keeps_planes_timestamp_and_matches_encoder_conversion() {
        ffmpeg::init().unwrap();
        let mut cache = None;
        for (width, height) in [
            (64, 64),
            (65, 63),
            (3, 3),
            (17, 7),
            (66, 70),
            (257, 255),
            (64, 64),
        ] {
            let pixels: Vec<u8> = (0..width * height)
                .flat_map(|i| [(i % 255) as u8, 80, 180, 255])
                .collect();
            let source = CapturedFrame {
                width,
                height,
                pixels,
                captured_at_unix_nanos: 123456,
                format: CapturePixelFormat::Bgra,
            };
            let mut expected = frame::Video::new(
                ffmpeg::format::Pixel::NV12,
                even_dimension(width),
                even_dimension(height),
            );
            let mut input = frame::Video::new(ffmpeg::format::Pixel::BGRA, width, height);
            copy_pixels(&source, &mut input).unwrap();
            let mut scaler = software::scaling::Context::get(
                ffmpeg::format::Pixel::BGRA,
                width,
                height,
                ffmpeg::format::Pixel::NV12,
                even_dimension(width),
                even_dimension(height),
                software::scaling::Flags::BILINEAR,
            )
            .unwrap();
            scaler.run(&input, &mut expected).unwrap();
            let prepared = prepare_nv12(&mut cache, source).unwrap();
            assert_eq!(prepared.format, CapturePixelFormat::Nv12);
            assert_eq!(prepared.captured_at_unix_nanos, 123456);
            assert_eq!(prepared.pixels.len(), prepared.pixel_bytes());
            let mut copied =
                frame::Video::new(ffmpeg::format::Pixel::NV12, prepared.width, prepared.height);
            copy_pixels(&prepared, &mut copied).unwrap();
            for plane in 0..2 {
                for row in 0..(prepared.height as usize >> plane) {
                    assert_eq!(
                        &copied.data(plane)[row * copied.stride(plane)
                            ..row * copied.stride(plane) + prepared.width as usize],
                        &expected.data(plane)[row * expected.stride(plane)
                            ..row * expected.stride(plane) + prepared.width as usize]
                    );
                }
            }
        }
    }

    #[test]
    fn direct_ffmpeg_encoder_writes_a_video_when_enabled() {
        if std::env::var_os("MQOL_TEST_FFMPEG").is_none() {
            return;
        }
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("test.mkv");
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
        let mut first = CapturedFrame {
            format: crate::CapturePixelFormat::Bgra,
            width: 64,
            height: 64,
            captured_at_unix_nanos: start,
            pixels: vec![0; 64 * 64 * 4],
        };
        let mut encoder = VideoFileEncoder::create(&config, &first).unwrap();
        for index in 0..30_u64 {
            first.captured_at_unix_nanos = start + index * 1_000_000_000 / 30;
            for pixel in first.pixels.chunks_exact_mut(4) {
                pixel[0] = (index * 7) as u8;
                pixel[1] = 80;
                pixel[2] = 180;
                pixel[3] = 255;
            }
            // Alternate normal and backlog frames, including a software YUV420P encoder.
            if index % 2 == 0 {
                encoder.encode(&first).unwrap();
            } else {
                let prepared = prepare_nv12(
                    &mut None,
                    CapturedFrame {
                        width: first.width,
                        height: first.height,
                        format: CapturePixelFormat::Bgra,
                        pixels: first.pixels.clone(),
                        captured_at_unix_nanos: first.captured_at_unix_nanos,
                    },
                )
                .unwrap();
                encoder.encode(&prepared).unwrap();
            }
        }
        encoder.finish().unwrap();
        drop(encoder);
        assert!(fs::metadata(path).unwrap().len() > 1_000);
    }
}
