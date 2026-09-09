//! SDL's native swap detour calls this on the owning GL thread, immediately BEFORE swap.
//! GL commands never run on the worker: PBOs + zero-timeout fences defer GPU readback.
use crate::{ERR_CAPTURE, ERR_INVALID_ARGUMENT, OK, ffi_status, set_last_error};
use std::cell::RefCell;
use std::collections::VecDeque;
use std::ffi::{c_char, c_void};
use std::ptr;
use std::sync::{Mutex, OnceLock};
use std::time::{Instant, SystemTime, UNIX_EPOCH};

const PACK_BUFFER: u32 = 0x88EB;
const READ_FRAMEBUFFER: u32 = 0x8CA8;
const RING_SIZE: usize = 3;
const MAX_BYTES: usize = 4096 * 4096 * 4;
pub(crate) fn pixel_bytes(w: u32, h: u32) -> Option<usize> {
    if w == 0 || h == 0 {
        return None;
    }
    let n = (w as usize).checked_mul(h as usize)?.checked_mul(4)?;
    (n <= MAX_BYTES).then_some(n)
}

// Absolute epoch for sidecars, advancing only by a monotonic clock (wall-clock jumps harmless).
#[unsafe(no_mangle)]
pub extern "C" fn mqol_source_clock_nanos() -> u64 {
    static CLOCK: OnceLock<(Instant, u64)> = OnceLock::new();
    let (instant, epoch) = CLOCK.get_or_init(|| {
        (
            Instant::now(),
            SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos() as u64,
        )
    });
    epoch.saturating_add(instant.elapsed().as_nanos() as u64)
}

type Resolve = unsafe extern "C" fn(*const c_char) -> *const c_void;
macro_rules! gl_api {
    ($($field:ident: $name:literal ($($arg:ty),*) -> $ret:ty;)*) => {
        struct Gl { $($field: unsafe extern "system" fn($($arg),*) -> $ret,)* }
        impl Gl {
            unsafe fn load(resolve: Resolve) -> Result<Self, String> {
                Ok(Self {$($field: {
                    let name = concat!($name, "\0");
                    // SAFETY: SDL resolver and current GL context supplied by the detour.
                    let address = unsafe { resolve(name.as_ptr().cast()) };
                    if address.is_null() { return Err(format!("SDL OpenGL readback requires {} (OpenGL 3.2 / GLES 3)", $name)); }
                    unsafe { std::mem::transmute::<*const c_void, unsafe extern "system" fn($($arg),*) -> $ret>(address) }
                },)*})
            }
        }
    }
}
gl_api! {
    get: "glGetIntegerv" (u32, *mut i32) -> ();
    generate: "glGenBuffers" (i32, *mut u32) -> ();
    delete: "glDeleteBuffers" (i32, *const u32) -> ();
    bind: "glBindBuffer" (u32, u32) -> ();
    data: "glBufferData" (u32, isize, *const c_void, u32) -> ();
    map: "glMapBufferRange" (u32, isize, isize, u32) -> *const u8;
    unmap: "glUnmapBuffer" (u32) -> u8;
    read: "glReadPixels" (i32, i32, i32, i32, u32, u32, *mut c_void) -> ();
    pack: "glPixelStorei" (u32, i32) -> ();
    read_buffer: "glReadBuffer" (u32) -> ();
    framebuffer: "glBindFramebuffer" (u32, u32) -> ();
    fence: "glFenceSync" (u32, u32) -> *mut c_void;
    wait: "glClientWaitSync" (*mut c_void, u32, u64) -> u32;
    delete_sync: "glDeleteSync" (*mut c_void) -> ();
}
struct Slot {
    buffer: u32,
    fence: *mut c_void,
    timestamp: u64,
    sequence: u64,
}
struct Readback {
    context: usize,
    gl: Gl,
    slots: Vec<Slot>,
    width: u32,
    height: u32,
}
thread_local! { static READBACK: RefCell<Option<Readback>> = const { RefCell::new(None) }; }
struct Frame {
    bytes: Vec<u8>,
    width: u32,
    height: u32,
    timestamp: u64,
    sequence: u64,
    bottom_up: bool,
    rgba: bool,
}
static FRAMES: Mutex<VecDeque<Frame>> = Mutex::new(VecDeque::new());

#[cfg(windows)]
pub(crate) fn enqueue_d3d(
    bytes: Vec<u8>,
    width: u32,
    height: u32,
    timestamp: u64,
    sequence: u64,
    rgba: bool,
) {
    if let Ok(mut frames) = FRAMES.try_lock() {
        if frames.len() == RING_SIZE {
            frames.pop_front();
        }
        frames.push_back(Frame {
            bytes,
            width,
            height,
            timestamp,
            sequence,
            bottom_up: false,
            rgba,
        });
    }
}

impl Readback {
    // Only call while this context is current. Context destruction otherwise owns cleanup.
    unsafe fn release(&mut self) {
        for slot in self.slots.drain(..) {
            unsafe {
                if !slot.fence.is_null() {
                    (self.gl.delete_sync)(slot.fence);
                }
                (self.gl.delete)(1, &slot.buffer);
            }
        }
    }
    unsafe fn frame(
        &mut self,
        width: u32,
        height: u32,
        timestamp: u64,
        sequence: u64,
    ) -> Result<(), String> {
        let bytes = pixel_bytes(width, height).ok_or("drawable exceeds 64 MiB readback limit")?;
        let gl = &self.gl;
        let mut previous = [0i32; 6];
        // Save all state affected by pack/read operations; FNA caches bindings itself.
        let keys = [0x88ED, 0x8CAA, 0x0D05, 0x0D02, 0x0D03, 0x0D04];
        unsafe {
            for (key, value) in keys.into_iter().zip(previous.iter_mut()) {
                (gl.get)(key, value);
            }
        }
        let mut default_read_buffer = 0;
        unsafe {
            (self.gl.framebuffer)(READ_FRAMEBUFFER, 0);
            (self.gl.get)(0x0C02, &mut default_read_buffer);
            (self.gl.read_buffer)(0x0405); // GL_BACK on both desktop GL and GLES3
        }
        let result = unsafe { self.frame_inner(width, height, bytes, timestamp, sequence) };
        unsafe {
            (self.gl.bind)(PACK_BUFFER, previous[0] as u32);
            (self.gl.framebuffer)(READ_FRAMEBUFFER, 0);
            (self.gl.read_buffer)(default_read_buffer as u32);
            (self.gl.framebuffer)(READ_FRAMEBUFFER, previous[1] as u32);
            for i in 2..6 {
                (self.gl.pack)(keys[i], previous[i]);
            }
        }
        result
    }
    unsafe fn frame_inner(
        &mut self,
        width: u32,
        height: u32,
        bytes: usize,
        timestamp: u64,
        sequence: u64,
    ) -> Result<(), String> {
        if self.width != width || self.height != height {
            unsafe {
                self.release();
            }
            self.width = width;
            self.height = height;
        }
        let gl = &self.gl;
        while self.slots.len() < RING_SIZE {
            let mut buffer = 0;
            unsafe {
                (gl.generate)(1, &mut buffer);
                (gl.bind)(PACK_BUFFER, buffer);
                (gl.data)(PACK_BUFFER, bytes as isize, ptr::null(), 0x88E1);
            }
            self.slots.push(Slot {
                buffer,
                fence: ptr::null_mut(),
                timestamp: 0,
                sequence: 0,
            });
        }
        // Retire in submission order. Never map an unsignaled PBO, never wait for the GPU.
        self.slots.sort_by_key(|slot| {
            if slot.fence.is_null() {
                u64::MAX
            } else {
                slot.sequence
            }
        });
        for slot in &mut self.slots {
            if slot.fence.is_null() {
                continue;
            }
            let status = unsafe { (gl.wait)(slot.fence, 0, 0) };
            if status == 0x911B {
                break;
            } // GL_TIMEOUT_EXPIRED
            if status != 0x911A && status != 0x911C {
                return Err("glClientWaitSync failed".into());
            }
            unsafe {
                (gl.bind)(PACK_BUFFER, slot.buffer);
                let mapped = (gl.map)(PACK_BUFFER, 0, bytes as isize, 1);
                if mapped.is_null() {
                    return Err("glMapBufferRange failed".into());
                }
                let pixels = std::slice::from_raw_parts(mapped, bytes).to_vec();
                let valid = (gl.unmap)(PACK_BUFFER) != 0;
                (gl.delete_sync)(slot.fence);
                slot.fence = ptr::null_mut();
                // Never let a worker stall the renderer. At most three CPU frames survive.
                if valid && let Ok(mut frames) = FRAMES.try_lock() {
                    if frames.len() == RING_SIZE {
                        frames.pop_front();
                    }
                    frames.push_back(Frame {
                        bytes: pixels,
                        width,
                        height,
                        timestamp: slot.timestamp,
                        sequence: slot.sequence,
                        bottom_up: true,
                        rgba: true,
                    });
                }
            }
        }
        let Some(slot) = self.slots.iter_mut().find(|slot| slot.fence.is_null()) else {
            return Ok(());
        };
        unsafe {
            (gl.bind)(PACK_BUFFER, slot.buffer);
            (gl.framebuffer)(READ_FRAMEBUFFER, 0);
            (gl.pack)(0x0D05, 1); // alignment
            (gl.pack)(0x0D02, 0); // row length
            (gl.pack)(0x0D03, 0); // skip rows
            (gl.pack)(0x0D04, 0); // skip pixels
            // RGBA is portable to GLES; worker flips and converts to BGRA for consumers.
            (gl.read)(
                0,
                0,
                width as i32,
                height as i32,
                0x1908,
                0x1401,
                ptr::null_mut(),
            );
            slot.fence = (gl.fence)(0x9117, 0);
        }
        if slot.fence.is_null() {
            return Err("glFenceSync failed".into());
        }
        slot.timestamp = timestamp;
        slot.sequence = sequence;
        Ok(())
    }
}

/// Caller must be in the matching current SDL GL context, before SDL_GL_SwapWindow.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_source_gl_frame(
    resolve: Option<Resolve>,
    context: usize,
    width: u32,
    height: u32,
    timestamp: u64,
    sequence: u64,
) -> i32 {
    ffi_status(|| {
        let resolve = resolve.ok_or(ERR_INVALID_ARGUMENT)?;
        if context == 0 {
            return Err(ERR_INVALID_ARGUMENT);
        }
        READBACK.with(|state| {
            let mut state = state.borrow_mut();
            if state.as_ref().is_none_or(|r| r.context != context) {
                // An old context's resources belong to its driver, never delete in a new context.
                let gl = unsafe { Gl::load(resolve) }.map_err(|e| {
                    set_last_error(e);
                    ERR_CAPTURE
                })?;
                *state = Some(Readback {
                    context,
                    gl,
                    slots: Vec::new(),
                    width: 0,
                    height: 0,
                });
            }
            unsafe {
                state
                    .as_mut()
                    .unwrap()
                    .frame(width, height, timestamp, sequence)
            }
            .map_err(|e| {
                set_last_error(e);
                ERR_CAPTURE
            })?;
            Ok(OK)
        })
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn mqol_source_gl_release(current_context: usize) -> i32 {
    ffi_status(|| {
        READBACK.with(|state| {
            if let Some(mut r) = state.borrow_mut().take() {
                if r.context == current_context {
                    unsafe {
                        r.release();
                    }
                }
            }
        });
        FRAMES.lock().unwrap_or_else(|p| p.into_inner()).clear();
        Ok(OK)
    })
}

#[repr(C)]
#[derive(Default)]
pub struct FrameResult {
    pub pixels: *mut u8,
    pub length: usize,
    pub width: u32,
    pub height: u32,
    pub timestamp: u64,
    pub sequence: u64,
}
fn convert_rgba(frame: &mut Frame) {
    let stride = frame.width as usize * 4;
    let height = frame.height as usize;
    for y in 0..if frame.bottom_up { height / 2 } else { 0 } {
        let (before, after) = frame.bytes.split_at_mut((height - 1 - y) * stride);
        before[y * stride..(y + 1) * stride].swap_with_slice(&mut after[..stride]);
    }
    for pixel in frame.bytes.chunks_exact_mut(4) {
        if frame.rgba {
            pixel.swap(0, 2);
        }
        pixel[3] = 255;
    }
}
/// Called from the source worker; returns 1 for a frame, 0 when empty, negative on error.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_source_poll(output: *mut FrameResult) -> i32 {
    ffi_status(|| {
        if output.is_null() {
            return Err(ERR_INVALID_ARGUMENT);
        }
        unsafe {
            ptr::write(output, FrameResult::default());
        }
        let frame = FRAMES.lock().unwrap_or_else(|p| p.into_inner()).pop_front();
        let Some(mut frame) = frame else {
            return Ok(0);
        };
        convert_rgba(&mut frame);
        let mut bytes = frame.bytes.into_boxed_slice();
        let result = FrameResult {
            pixels: bytes.as_mut_ptr(),
            length: bytes.len(),
            width: frame.width,
            height: frame.height,
            timestamp: frame.timestamp,
            sequence: frame.sequence,
        };
        std::mem::forget(bytes);
        unsafe {
            ptr::write(output, result);
        }
        Ok(1)
    })
}
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_source_frame_free(pixels: *mut u8, length: usize) {
    if !pixels.is_null() {
        unsafe {
            drop(Box::from_raw(ptr::slice_from_raw_parts_mut(pixels, length)));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn bounded_dimensions() {
        assert_eq!(pixel_bytes(0, 10), None);
        assert_eq!(pixel_bytes(u32::MAX, u32::MAX), None);
        assert_eq!(pixel_bytes(1920, 1080), Some(8294400));
    }
    #[test]
    fn worker_flips_and_converts_pixels() {
        let mut frame = Frame {
            bytes: vec![255, 0, 0, 0, 0, 255, 0, 0, 0, 0, 255, 0, 255, 255, 255, 0],
            width: 2,
            height: 2,
            timestamp: 5,
            sequence: 9,
            bottom_up: true,
            rgba: true,
        };
        convert_rgba(&mut frame);
        assert_eq!(
            frame.bytes,
            vec![
                255, 0, 0, 255, 255, 255, 255, 255, 0, 0, 255, 255, 0, 255, 0, 255
            ]
        );
        assert_eq!((frame.timestamp, frame.sequence), (5, 9));
    }
    #[test]
    fn clock_is_monotonic() {
        let a = mqol_source_clock_nanos();
        assert!(mqol_source_clock_nanos() >= a);
    }
}

#[cfg(test)]
mod fake_gl_tests {
    use super::*;
    use std::collections::HashMap;
    #[derive(Default)]
    struct Mock {
        state: HashMap<u32, i32>,
        buffers: HashMap<u32, Vec<u8>>,
        next: u32,
        ready: bool,
        maps: usize,
        reads: usize,
        deleted: usize,
    }
    thread_local! { static MOCK: RefCell<Mock> = RefCell::new(Mock::default()); }
    unsafe extern "system" fn get(key: u32, value: *mut i32) {
        unsafe {
            *value = MOCK.with(|m| *m.borrow().state.get(&key).unwrap_or(&0));
        }
    }
    unsafe extern "system" fn generate(_: i32, output: *mut u32) {
        MOCK.with(|m| {
            let mut m = m.borrow_mut();
            m.next += 1;
            unsafe {
                *output = m.next;
            }
        });
    }
    unsafe extern "system" fn delete(_: i32, value: *const u32) {
        MOCK.with(|m| {
            let mut m = m.borrow_mut();
            m.buffers.remove(&unsafe { *value });
            m.deleted += 1;
        });
    }
    unsafe extern "system" fn bind(_: u32, value: u32) {
        MOCK.with(|m| {
            m.borrow_mut().state.insert(0x88ED, value as i32);
        });
    }
    unsafe extern "system" fn data(_: u32, length: isize, _: *const c_void, _: u32) {
        MOCK.with(|m| {
            let mut m = m.borrow_mut();
            let b = m.state[&0x88ED] as u32;
            m.buffers.insert(b, vec![0; length as usize]);
        });
    }
    unsafe extern "system" fn map(_: u32, _: isize, _: isize, _: u32) -> *const u8 {
        MOCK.with(|m| {
            let mut m = m.borrow_mut();
            m.maps += 1;
            m.buffers[&(m.state[&0x88ED] as u32)].as_ptr()
        })
    }
    unsafe extern "system" fn unmap(_: u32) -> u8 {
        1
    }
    unsafe extern "system" fn read(_: i32, _: i32, _: i32, _: i32, _: u32, _: u32, _: *mut c_void) {
        MOCK.with(|m| {
            let mut m = m.borrow_mut();
            m.reads += 1;
            let id = m.reads as u8;
            let b = m.state[&0x88ED] as u32;
            m.buffers.get_mut(&b).unwrap().fill(id);
        });
    }
    unsafe extern "system" fn pack(key: u32, value: i32) {
        MOCK.with(|m| {
            m.borrow_mut().state.insert(key, value);
        });
    }
    unsafe extern "system" fn framebuffer(_: u32, value: u32) {
        unsafe { pack(0x8CAA, value as i32) }
    }
    unsafe extern "system" fn read_buffer(value: u32) {
        unsafe { pack(0x0C02, value as i32) }
    }
    unsafe extern "system" fn fence(_: u32, _: u32) -> *mut c_void {
        1usize as *mut c_void
    }
    unsafe extern "system" fn wait(_: *mut c_void, flags: u32, timeout: u64) -> u32 {
        assert_eq!((flags, timeout), (0, 0));
        MOCK.with(|m| if m.borrow().ready { 0x911A } else { 0x911B })
    }
    unsafe extern "system" fn delete_sync(_: *mut c_void) {}
    #[test]
    fn pbo_ring_never_maps_pending_gpu_work_restores_state_and_handles_resize() {
        FRAMES.lock().unwrap().clear();
        let before = HashMap::from([
            (0x88ED, 77),
            (0x8CAA, 88),
            (0x0D05, 8),
            (0x0D02, 9),
            (0x0D03, 3),
            (0x0D04, 4),
            (0x0C02, 0x0404),
        ]);
        MOCK.with(|m| {
            *m.borrow_mut() = Mock {
                state: before.clone(),
                ..Default::default()
            }
        });
        let gl = Gl {
            get,
            generate,
            delete,
            bind,
            data,
            map,
            unmap,
            read,
            pack,
            framebuffer,
            read_buffer,
            fence,
            wait,
            delete_sync,
        };
        let mut r = Readback {
            context: 1,
            gl,
            slots: vec![],
            width: 0,
            height: 0,
        };
        for n in 1..=5 {
            unsafe {
                r.frame(2, 2, n * 10, n).unwrap();
            }
        }
        MOCK.with(|m| {
            let m = m.borrow();
            assert_eq!(m.maps, 0);
            assert_eq!(m.reads, 3);
            assert_eq!(m.state, before);
        });
        MOCK.with(|m| m.borrow_mut().ready = true);
        unsafe {
            r.frame(2, 2, 60, 6).unwrap();
        }
        let mut output = FrameResult::default();
        for n in 1..=3 {
            assert_eq!(unsafe { mqol_source_poll(&mut output) }, 1);
            assert_eq!((output.timestamp, output.sequence), (n * 10, n));
            assert_eq!(unsafe { *output.pixels }, n as u8);
            unsafe {
                mqol_source_frame_free(output.pixels, output.length);
            }
        }
        assert_eq!(unsafe { mqol_source_poll(&mut output) }, 0);
        unsafe {
            r.frame(3, 3, 70, 7).unwrap();
        }
        MOCK.with(|m| {
            let m = m.borrow();
            assert_eq!(m.deleted, 3);
            assert_eq!(m.state, before);
        });
        unsafe {
            r.release();
        }
        assert!(r.slots.is_empty());
    }
}
