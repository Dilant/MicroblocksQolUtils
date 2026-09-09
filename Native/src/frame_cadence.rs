//! Cap GPU submissions only when ALL pixel subscribers request a finite rate.
//! Pending readbacks still retire on every presentation; zero means unrestricted.
use std::sync::atomic::{AtomicU32, Ordering};
static RATE: AtomicU32 = AtomicU32::new(0);

#[derive(Default)]
pub(crate) struct FrameCadence {
    rate: u32,
    origin: Option<u64>,
    tick: u128,
}
impl FrameCadence {
    pub(crate) fn due(&mut self, timestamp: u64) -> bool {
        self.at_rate(timestamp, RATE.load(Ordering::Relaxed))
    }
    fn at_rate(&mut self, timestamp: u64, rate: u32) -> bool {
        if self.rate != rate {
            *self = Self {
                rate,
                ..Default::default()
            };
        }
        if rate == 0 {
            return true;
        }
        let Some(origin) = self.origin else {
            self.origin = Some(timestamp);
            return true;
        };
        let tick = crate::video_tick(timestamp, origin, rate);
        if tick <= self.tick {
            return false;
        }
        self.tick = tick;
        true
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn mqol_source_set_frame_rate(rate: u32) -> i32 {
    if rate > 240 {
        return crate::ERR_INVALID_ARGUMENT;
    }
    RATE.store(rate, Ordering::Relaxed);
    crate::OK
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn readback_sampling_preserves_requested_rate_and_unlimited_callbacks() {
        for input in [120, 144, 240] {
            let mut gate = FrameCadence::default();
            let retained = (0..input * 10u64)
                .filter(|i| gate.at_rate(i * 1_000_000_000 / input, 60))
                .count();
            assert!((600..=601).contains(&retained));
            for i in 0..input {
                assert!(gate.at_rate(i * 1_000_000_000 / input, 0));
            }
            assert!(gate.at_rate(0, 30));
            assert!(!gate.at_rate(1_000_000, 30));
        }
    }
}
