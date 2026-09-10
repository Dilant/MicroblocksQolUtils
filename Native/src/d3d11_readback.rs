//! DXGI Present is the actual SDL/FNA D3D11 presentation boundary; it does not
//! call SDL_GL_SwapWindow. Copy to staging now, only map completed queries later.
use crate::sdl_readback::{enqueue_d3d, pixel_bytes};
use crate::{ERR_CAPTURE, ERR_INVALID_ARGUMENT, OK, ffi_status, set_last_error};
use std::sync::{
    OnceLock,
    atomic::{AtomicBool, AtomicUsize, Ordering},
};
use std::{cell::RefCell, ffi::c_void, ptr};
use windows::{
    Win32::{
        Foundation::{HMODULE, HWND, S_OK},
        Graphics::{
            Direct3D::D3D_DRIVER_TYPE_HARDWARE,
            Direct3D11::*,
            Dxgi::{Common::*, *},
        },
        System::{
            LibraryLoader::{
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, GET_MODULE_HANDLE_EX_FLAG_PIN,
                GetModuleHandleExW, LoadLibraryW,
            },
            Memory::*,
        },
        UI::WindowsAndMessaging::*,
    },
    core::{Interface, PCWSTR, w},
};

struct ProbeWindow(HWND);
impl Drop for ProbeWindow {
    fn drop(&mut self) {
        unsafe {
            let _ = DestroyWindow(self.0);
        }
    }
}

// A private, never-shown window discovers DXGI's Present implementation without
// touching the game's device or renderer selection. No swapchain is retained.
unsafe fn probe() -> windows::core::Result<(ProbeWindow, IDXGISwapChain)> {
    unsafe {
        let window = ProbeWindow(CreateWindowExW(
            WINDOW_EX_STYLE(0),
            w!("STATIC"),
            w!("MQOL DXGI probe"),
            WS_POPUP,
            0,
            0,
            16,
            16,
            HWND::default(),
            None,
            None,
            None,
        )?);
        let desc = DXGI_SWAP_CHAIN_DESC {
            BufferDesc: DXGI_MODE_DESC {
                Width: 16,
                Height: 16,
                Format: DXGI_FORMAT_R8G8B8A8_UNORM,
                ..Default::default()
            },
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
            BufferCount: 2,
            OutputWindow: window.0,
            Windowed: true.into(),
            SwapEffect: DXGI_SWAP_EFFECT_DISCARD,
            ..Default::default()
        };
        let mut chain = None;
        D3D11CreateDeviceAndSwapChain(
            None,
            D3D_DRIVER_TYPE_HARDWARE,
            HMODULE::default(),
            D3D11_CREATE_DEVICE_FLAG(0),
            None,
            D3D11_SDK_VERSION,
            Some(&desc),
            Some(&mut chain),
            None,
            None,
            None,
        )?;
        Ok((window, chain.unwrap()))
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_source_dxgi_install(
    callback: Option<unsafe extern "C" fn(*mut c_void, u32, u32)>,
) -> i32 {
    ffi_status(|| {
        let callback = callback.ok_or(ERR_INVALID_ARGUMENT)?;
        if CHAIN_RETAINED.load(Ordering::SeqCst) {
            set_last_error(
                "A later DXGI hook retained the capture shim; restart the game before reloading capture",
            );
            return Err(ERR_CAPTURE);
        }
        if PRESENT_SLOT.load(Ordering::SeqCst) != 0 {
            return Err(ERR_INVALID_ARGUMENT);
        }
        // Keep the shared vtable/module mapped even when the disposable probe is released.
        static DXGI: OnceLock<usize> = OnceLock::new();
        DXGI.get_or_init(|| unsafe {
            LoadLibraryW(w!("dxgi.dll"))
                .map(|h| h.0 as usize)
                .unwrap_or(0)
        });
        let (_window, chain) = unsafe { probe() }.map_err(|e| {
            set_last_error(e.to_string());
            ERR_CAPTURE
        })?;
        let slot = ptr::addr_of!(chain.vtable().Present) as *mut AtomicUsize;
        // A later overlay can retain our shim after managed mod unload. Pin the native
        // module so that no-op shim cannot become a dangling function pointer.
        let mut module = HMODULE::default();
        unsafe {
            GetModuleHandleExW(
                GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                PCWSTR(present_shim as *const () as *const u16),
                &mut module,
            )
        }
        .map_err(|e| {
            set_last_error(e.to_string());
            ERR_CAPTURE
        })?;
        let original = unsafe { (*slot).load(Ordering::SeqCst) };
        if std::env::var_os("MICROBLOCKS_QOL_CAPTURE_TRACE").is_some() {
            eprintln!(
                "MQOL DXGI slot={slot:p} original={original:x} shim={:x}",
                present_shim as *const () as usize
            );
        }
        ORIGINAL_PRESENT.store(original, Ordering::SeqCst);
        unsafe { replace_slot(slot, original, present_shim as *const () as usize) }.map_err(
            |e| {
                set_last_error(e.to_string());
                ERR_CAPTURE
            },
        )?;
        PRESENT_SLOT.store(slot as usize, Ordering::SeqCst);
        PRESENT_CALLBACK.store(callback as usize, Ordering::SeqCst);
        Ok(OK)
    })
}

static PRESENT_SLOT: AtomicUsize = AtomicUsize::new(0);
static ORIGINAL_PRESENT: AtomicUsize = AtomicUsize::new(0);
static PRESENT_CALLBACK: AtomicUsize = AtomicUsize::new(0);
static CALLBACKS_ACTIVE: AtomicUsize = AtomicUsize::new(0);
static CHAIN_RETAINED: AtomicBool = AtomicBool::new(false);
unsafe fn replace_slot(
    slot: *mut AtomicUsize,
    expected: usize,
    replacement: usize,
) -> windows::core::Result<()> {
    unsafe {
        let mut protection = PAGE_PROTECTION_FLAGS(0);
        VirtualProtect(
            slot.cast(),
            std::mem::size_of::<usize>(),
            PAGE_READWRITE,
            &mut protection,
        )?;
        let swapped =
            (*slot).compare_exchange(expected, replacement, Ordering::SeqCst, Ordering::SeqCst);
        let mut ignored = PAGE_PROTECTION_FLAGS(0);
        VirtualProtect(
            slot.cast(),
            std::mem::size_of::<usize>(),
            protection,
            &mut ignored,
        )?;
        swapped.map_err(|_| {
            windows::core::Error::from_hresult(windows::Win32::Foundation::E_UNEXPECTED)
        })?;
        Ok(())
    }
}
unsafe extern "system" fn present_shim(
    chain: *mut c_void,
    interval: u32,
    flags: u32,
) -> windows::core::HRESULT {
    CALLBACKS_ACTIVE.fetch_add(1, Ordering::SeqCst);
    let callback = PRESENT_CALLBACK.load(Ordering::SeqCst);
    if callback != 0 {
        let callback: unsafe extern "C" fn(*mut c_void, u32, u32) =
            unsafe { std::mem::transmute(callback) };
        unsafe {
            callback(chain, interval, flags);
        }
    }
    CALLBACKS_ACTIVE.fetch_sub(1, Ordering::SeqCst);
    let original: unsafe extern "system" fn(*mut c_void, u32, u32) -> windows::core::HRESULT =
        unsafe { std::mem::transmute(ORIGINAL_PRESENT.load(Ordering::SeqCst)) };
    unsafe { original(chain, interval, flags) }
}

#[unsafe(no_mangle)]
pub extern "C" fn mqol_source_dxgi_uninstall() -> i32 {
    ffi_status(|| {
        PRESENT_CALLBACK.store(0, Ordering::SeqCst);
        let slot = PRESENT_SLOT.swap(0, Ordering::SeqCst) as *mut AtomicUsize;
        if !slot.is_null()
            && unsafe { (*slot).load(Ordering::SeqCst) } == present_shim as *const () as usize
        {
            unsafe {
                replace_slot(
                    slot,
                    present_shim as *const () as usize,
                    ORIGINAL_PRESENT.load(Ordering::SeqCst),
                )
            }
            .map_err(|e| {
                set_last_error(e.to_string());
                ERR_CAPTURE
            })?;
        } else if !slot.is_null() {
            CHAIN_RETAINED.store(true, Ordering::SeqCst);
        }
        // If another hook chained after us, leave its slot alone. The native shim remains
        // callable with callback=0, so it never points at an unloaded managed delegate.
        while CALLBACKS_ACTIVE.load(Ordering::SeqCst) != 0 {
            std::thread::yield_now();
        }
        Ok(OK)
    })
}

struct Slot {
    texture: ID3D11Texture2D,
    query: ID3D11Query,
    pending: bool,
    timestamp: u64,
    sequence: u64,
    route_mask: u32,
    route_version: u64,
}
struct Readback {
    cadence: crate::frame_cadence::FrameCadence,
    device: ID3D11Device,
    context: ID3D11DeviceContext,
    width: u32,
    height: u32,
    format: DXGI_FORMAT,
    slots: Vec<Slot>,
}
thread_local! { static READBACK: RefCell<Option<Readback>> = const { RefCell::new(None) }; }

impl Readback {
    unsafe fn new(device: ID3D11Device, desc: D3D11_TEXTURE2D_DESC) -> windows::core::Result<Self> {
        unsafe {
            let context = device.GetImmediateContext()?;
            let mut slots = Vec::with_capacity(3);
            let staging = D3D11_TEXTURE2D_DESC {
                Usage: D3D11_USAGE_STAGING,
                BindFlags: 0,
                CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
                MiscFlags: 0,
                ..desc
            };
            for _ in 0..3 {
                let mut texture = None;
                let mut query = None;
                device.CreateTexture2D(&staging, None, Some(&mut texture))?;
                device.CreateQuery(
                    &D3D11_QUERY_DESC {
                        Query: D3D11_QUERY_EVENT,
                        MiscFlags: 0,
                    },
                    Some(&mut query),
                )?;
                slots.push(Slot {
                    texture: texture.unwrap(),
                    query: query.unwrap(),
                    pending: false,
                    timestamp: 0,
                    sequence: 0,
                    route_mask: 0,
                    route_version: 0,
                });
            }
            Ok(Self {
                cadence: Default::default(),
                device,
                context,
                width: desc.Width,
                height: desc.Height,
                format: desc.Format,
                slots,
            })
        }
    }
    unsafe fn frame(
        &mut self,
        backbuffer: &ID3D11Texture2D,
        timestamp: u64,
        sequence: u64,
    ) -> windows::core::Result<()> {
        unsafe {
            // Oldest first, even after wrapping the ring. Preserve presentation order.
            self.slots
                .sort_by_key(|s| if s.pending { s.sequence } else { u64::MAX });
            for slot in self.slots.iter_mut().filter(|s| s.pending) {
                // Windows bindings' Result<()> loses S_FALSE. Inspect raw HRESULT so
                // an unsignaled query is NEVER treated as completed GPU work.
                let status = (self.context.vtable().GetData)(
                    self.context.as_raw(),
                    slot.query.as_raw(),
                    ptr::null_mut(),
                    0,
                    D3D11_ASYNC_GETDATA_DONOTFLUSH.0 as u32,
                );
                if status != S_OK {
                    status.ok()?;
                    break;
                }
                let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
                match self.context.Map(
                    &slot.texture,
                    0,
                    D3D11_MAP_READ,
                    D3D11_MAP_FLAG_DO_NOT_WAIT.0 as u32,
                    Some(&mut mapped),
                ) {
                    Err(e) if e.code() == DXGI_ERROR_WAS_STILL_DRAWING => break,
                    Err(e) => return Err(e),
                    Ok(()) => {}
                }
                let stride = self.width as usize * 4;
                let mut pixels = vec![0; stride * self.height as usize];
                for y in 0..self.height as usize {
                    ptr::copy_nonoverlapping(
                        (mapped.pData as *const u8).add(y * mapped.RowPitch as usize),
                        pixels.as_mut_ptr().add(y * stride),
                        stride,
                    );
                }
                self.context.Unmap(&slot.texture, 0);
                slot.pending = false;
                let rgba = matches!(
                    self.format,
                    DXGI_FORMAT_R8G8B8A8_UNORM | DXGI_FORMAT_R8G8B8A8_UNORM_SRGB
                );
                enqueue_d3d(
                    pixels,
                    self.width,
                    self.height,
                    slot.timestamp,
                    slot.sequence,
                    slot.route_mask,
                    slot.route_version,
                    rgba,
                );
            }
            if let Some(slot) = self.slots.iter_mut().find(|s| !s.pending) {
                let (route_mask, route_version) = self.cadence.select(timestamp);
                if route_mask == 0 {
                    return Ok(());
                }
                self.context.CopyResource(&slot.texture, backbuffer);
                self.context.End(&slot.query);
                slot.pending = true;
                slot.timestamp = timestamp;
                slot.sequence = sequence;
                slot.route_mask = route_mask;
                slot.route_version = route_version;
            }
            Ok(())
        }
    }
}

/// Returns 1 only for the chosen SDL window (also when capture is disabled), 0 for other swapchains.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_source_d3d11_frame(
    raw: *mut c_void,
    window: *mut c_void,
    enabled: u32,
    timestamp: u64,
    sequence: u64,
) -> i32 {
    ffi_status(|| {
        if raw.is_null() || window.is_null() {
            return Err(ERR_INVALID_ARGUMENT);
        }
        let result = unsafe {
            (|| -> Result<i32, String> {
                let chain = IDXGISwapChain::from_raw_borrowed(&raw).unwrap();
                let chain_desc = chain.GetDesc().map_err(|e| e.to_string())?;
                if chain_desc.OutputWindow.0 != window {
                    return Ok(0);
                }
                READBACK.with(|cell| {
                let mut state = cell.borrow_mut();
                if enabled == 0 { *state = None; return Ok(1); }
                let buffer: ID3D11Texture2D = chain.GetBuffer(0).map_err(|e| e.to_string())?;
                let mut desc = D3D11_TEXTURE2D_DESC::default(); buffer.GetDesc(&mut desc);
                if pixel_bytes(desc.Width, desc.Height).is_none() { return Err("D3D11 backbuffer exceeds 64 MiB readback limit".into()); }
                if desc.SampleDesc.Count != 1 || !matches!(desc.Format,
                    DXGI_FORMAT_R8G8B8A8_UNORM | DXGI_FORMAT_R8G8B8A8_UNORM_SRGB |
                    DXGI_FORMAT_B8G8R8A8_UNORM | DXGI_FORMAT_B8G8R8A8_UNORM_SRGB) {
                    return Err(format!("Unsupported D3D11 swapchain format {:?}/{} samples; SDR RGBA/BGRA is required", desc.Format, desc.SampleDesc.Count));
                }
                let device: ID3D11Device = chain.GetDevice().map_err(|e| e.to_string())?;
                if state.as_ref().is_none_or(|s| s.width != desc.Width || s.height != desc.Height || s.format != desc.Format || s.device != device) {
                    *state = Some(Readback::new(device, desc).map_err(|e| e.to_string())?);
                }
                state.as_mut().unwrap().frame(&buffer, timestamp, sequence).map_err(|e| e.to_string())?;
                // `buffer` is dropped before Present: never retain a swapchain texture across ResizeBuffers.
                Ok(1)
            })
            })()
        };
        result.map_err(|e| {
            set_last_error(e);
            ERR_CAPTURE
        })
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mqol_source_d3d11_release() -> i32 {
    ffi_status(|| {
        READBACK.with(|s| *s.borrow_mut() = None);
        Ok(OK)
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    static PRESENTS: AtomicUsize = AtomicUsize::new(0);
    unsafe extern "C" fn on_present(_: *mut c_void, _: u32, _: u32) {
        PRESENTS.fetch_add(1, Ordering::SeqCst);
    }
    #[test]
    fn real_dxgi_hook_calls_original_and_unhooks() {
        if std::env::var_os("MQOL_TEST_D3D11").is_none() {
            return;
        }
        unsafe {
            let (_window, chain) = probe().unwrap();
            assert_eq!(mqol_source_dxgi_install(Some(on_present)), 0);
            let _ = chain.Present(0, DXGI_PRESENT(0));
            assert_eq!(PRESENTS.load(Ordering::SeqCst), 1);
            assert_eq!(mqol_source_dxgi_uninstall(), 0);
            let _ = chain.Present(0, DXGI_PRESENT(0));
            assert_eq!(PRESENTS.load(Ordering::SeqCst), 1);
        }
    }
    use crate::sdl_readback::{FrameResult, mqol_source_frame_free, mqol_source_poll};
    #[test]
    #[ignore = "full-resolution hardware regression: MQOL_TEST_BGRA and MQOL_TEST_OUTPUT, --release"]
    #[cfg(feature = "ffmpeg")]
    fn real_d3d11_full_resolution_recording_cadence() {
        use crate::{CaptureConfig, CaptureSession, CapturedFrame};
        use std::sync::{Arc, atomic::AtomicU64};
        use std::time::{Duration, Instant};
        let width = 2560u32;
        let height = 1506u32;
        let pixels = std::fs::read(std::env::var("MQOL_TEST_BGRA").unwrap()).unwrap();
        assert_eq!(pixels.len(), width as usize * height as usize * 4);
        let session = Arc::new(CaptureSession::new(CaptureConfig {
            fps: 60,
            encoder: "auto".into(),
            bitrate_kbps: 12000,
            output_path: Some(std::env::var("MQOL_TEST_OUTPUT").unwrap()),
            ..Default::default()
        }));
        session.start().unwrap();
        crate::frame_cadence::mqol_source_set_frame_rate(60);
        let running = Arc::new(AtomicBool::new(true));
        let delivered = Arc::new(AtomicU64::new(0));
        // Match production: acquisition and sink preparation are separate workers.
        // Combining poll/conversion and encoding push here creates a bottleneck
        // that the actual CaptureSource/Subscription pipeline does not have.
        let (sender, receiver) = std::sync::mpsc::sync_channel::<CapturedFrame>(5);
        let sink = {
            let session = session.clone();
            std::thread::spawn(move || {
                for frame in receiver {
                    session.push_frame(frame);
                }
            })
        };
        let consumer = {
            let running = running.clone();
            let delivered = delivered.clone();
            std::thread::spawn(move || {
                while running.load(Ordering::Acquire) {
                    let mut frame = FrameResult::default();
                    if unsafe { mqol_source_poll(&mut frame) } == 1 {
                        delivered.fetch_add(1, Ordering::Relaxed);
                        {
                            let bgra =
                                unsafe { std::slice::from_raw_parts(frame.pixels, frame.length) }
                                    .to_vec();
                            sender
                                .try_send(CapturedFrame {
                                    format: crate::CapturePixelFormat::Bgra,
                                    width: frame.width,
                                    height: frame.height,
                                    captured_at_unix_nanos: frame.timestamp,
                                    pixels: bgra,
                                })
                                .expect("bounded source-to-sink delivery overflowed");
                        }
                        unsafe {
                            mqol_source_frame_free(frame.pixels, frame.length);
                        }
                    } else {
                        std::thread::sleep(Duration::from_millis(1));
                    }
                }
            })
        };
        let watch = Instant::now();
        unsafe {
            let (window, chain) = probe().unwrap();
            chain
                .ResizeBuffers(
                    2,
                    width,
                    height,
                    DXGI_FORMAT_UNKNOWN,
                    DXGI_SWAP_CHAIN_FLAG(0),
                )
                .unwrap();
            let device: ID3D11Device = chain.GetDevice().unwrap();
            let context = device.GetImmediateContext().unwrap();
            let buffer: ID3D11Texture2D = chain.GetBuffer(0).unwrap();
            let mut desc = D3D11_TEXTURE2D_DESC::default();
            buffer.GetDesc(&mut desc);
            desc.BindFlags = 0;
            desc.MiscFlags = 0;
            let initial = D3D11_SUBRESOURCE_DATA {
                pSysMem: pixels.as_ptr().cast(),
                SysMemPitch: width * 4,
                SysMemSlicePitch: 0,
            };
            let mut picture = None;
            device
                .CreateTexture2D(&desc, Some(&initial), Some(&mut picture))
                .unwrap();
            let picture = picture.unwrap();
            let mut marker = vec![255u8; 512 * 32 * 4];
            let marker_box = D3D11_BOX {
                left: 0,
                top: 0,
                front: 0,
                right: 512,
                bottom: 32,
                back: 1,
            };
            let start = Instant::now();
            for i in 0..2400u64 {
                let due = start + Duration::from_nanos(i * 1_000_000_000 / 120);
                if let Some(wait) = due.checked_duration_since(Instant::now()) {
                    std::thread::sleep(wait);
                }
                context.CopyResource(&buffer, &picture);
                for y in 0..32 {
                    for x in 0..512 {
                        marker[(y * 512 + x) * 4..(y * 512 + x) * 4 + 3]
                            .fill(if (i >> (x / 32)) & 1 == 0 { 0 } else { 255 });
                    }
                }
                context.UpdateSubresource(
                    &buffer,
                    0,
                    Some(&marker_box),
                    marker.as_ptr().cast(),
                    512 * 4,
                    0,
                );
                assert_eq!(
                    mqol_source_d3d11_frame(
                        chain.as_raw(),
                        window.0.0,
                        1,
                        crate::sdl_readback::mqol_source_clock_nanos(),
                        i + 1
                    ),
                    1
                );
                context.Flush();
                let _ = chain.Present(0, DXGI_PRESENT(0));
                if i % 240 == 239 {
                    eprintln!(
                        "d3d cadence wall={:?} delivered={} {:?}",
                        start.elapsed(),
                        delivered.load(Ordering::Relaxed),
                        session.stats()
                    );
                }
            }
            assert!(
                start.elapsed().as_secs_f64() < 21.0,
                "renderer below 120 FPS"
            );
            mqol_source_d3d11_release();
        }
        std::thread::sleep(Duration::from_millis(100));
        running.store(false, Ordering::Release);
        consumer.join().unwrap();
        sink.join().unwrap();
        session.stop().unwrap();
        crate::frame_cadence::mqol_source_set_frame_rate(0);
        let stats = session.stats();
        eprintln!(
            "d3d cadence final wall={:?} delivered={} {stats:?}",
            watch.elapsed(),
            delivered.load(Ordering::Relaxed)
        );
        assert_eq!(stats.frames_dropped, 0);
        assert_eq!(stats.frames_consumed, stats.frames_captured);
        assert!(
            stats.frames_captured as f64 > stats.media_time_nanos as f64 / 1e9 * 60.0 * 0.995,
            "readback/encoder path below 60 FPS"
        );
    }

    #[test]
    fn real_d3d11_staging_query_pixels_resize_and_release() {
        if std::env::var_os("MQOL_TEST_D3D11").is_none() {
            return;
        }
        unsafe {
            let (window, chain) = probe().unwrap();
            let device: ID3D11Device = chain.GetDevice().unwrap();
            let context = device.GetImmediateContext().unwrap();
            let mut captured = 0;
            for sequence in 1..100 {
                if sequence == 40 {
                    chain
                        .ResizeBuffers(2, 24, 18, DXGI_FORMAT_UNKNOWN, DXGI_SWAP_CHAIN_FLAG(0))
                        .unwrap();
                }
                {
                    let buffer: ID3D11Texture2D = chain.GetBuffer(0).unwrap();
                    let mut view = None;
                    device
                        .CreateRenderTargetView(&buffer, None, Some(&mut view))
                        .unwrap();
                    context.ClearRenderTargetView(view.as_ref().unwrap(), &[1.0, 0.0, 0.0, 1.0]);
                }
                assert_eq!(
                    mqol_source_d3d11_frame(
                        chain.as_raw(),
                        window.0.0,
                        1,
                        sequence * 1000,
                        sequence
                    ),
                    1
                );
                context.Flush(); // Hidden probe windows may not submit on an occluded Present.
                let _ = chain.Present(0, DXGI_PRESENT(0));
                std::thread::sleep(std::time::Duration::from_millis(2));
                let mut frame = FrameResult::default();
                while mqol_source_poll(&mut frame) == 1 {
                    assert_eq!(frame.timestamp, frame.sequence * 1000);
                    assert_eq!(
                        (frame.width, frame.height),
                        if frame.sequence < 40 {
                            (16, 16)
                        } else {
                            (24, 18)
                        }
                    );
                    let bytes = std::slice::from_raw_parts(frame.pixels, frame.length);
                    assert!(
                        bytes.chunks_exact(4).all(|p| p == [0, 0, 255, 255]),
                        "BGRA conversion/row pitch"
                    );
                    mqol_source_frame_free(frame.pixels, frame.length);
                    captured += 1;
                }
            }
            assert!(captured > 50, "no completed staging frames: {captured}");
            assert_eq!(
                mqol_source_d3d11_frame(chain.as_raw(), window.0.0, 0, 0, 0),
                1
            );
            assert_eq!(mqol_source_d3d11_release(), 0);
            chain
                .ResizeBuffers(2, 32, 16, DXGI_FORMAT_UNKNOWN, DXGI_SWAP_CHAIN_FLAG(0))
                .unwrap();
        }
    }
}
