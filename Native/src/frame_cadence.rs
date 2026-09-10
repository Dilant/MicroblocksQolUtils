//! The only frame-rate selector. Select once per requested rate before GPU
//! readback; carry the recipient mask with that image through every queue.
use std::sync::Mutex;

pub(crate) const NO_ROUTE: u32 = u32::MAX;
pub(crate) const ROUTE_COUNT: usize = 16;
// Route zero keeps direct/native callers working until managed routes are set.
static ROUTES: Mutex<([u32; ROUTE_COUNT], u64)> = Mutex::new((
    [
        0, NO_ROUTE, NO_ROUTE, NO_ROUTE, NO_ROUTE, NO_ROUTE, NO_ROUTE, NO_ROUTE, NO_ROUTE,
        NO_ROUTE, NO_ROUTE, NO_ROUTE, NO_ROUTE, NO_ROUTE, NO_ROUTE, NO_ROUTE,
    ],
    0,
));

#[derive(Default)]
pub(crate) struct FrameCadence {
    clocks: Vec<(u32, u128)>,
}
impl FrameCadence {
    pub(crate) fn select(&mut self, timestamp: u64) -> (u32, u64) {
        let (routes, version) = *ROUTES.lock().unwrap_or_else(|p| p.into_inner());
        (self.select_routes(timestamp, &routes), version)
    }

    fn select_routes(&mut self, timestamp: u64, routes: &[u32; ROUTE_COUNT]) -> u32 {
        self.clocks.retain(|(rate, _)| routes.contains(rate));
        let mut selected = 0;
        for (index, &rate) in routes.iter().enumerate() {
            if rate == NO_ROUTE || routes[..index].contains(&rate) {
                continue;
            }
            let due = if rate == 0 {
                true
            } else {
                let tick = crate::video_tick(timestamp, 0, rate);
                if let Some((_, last)) = self.clocks.iter_mut().find(|(r, _)| *r == rate) {
                    if tick <= *last {
                        false
                    } else {
                        *last = tick;
                        true
                    }
                } else {
                    self.clocks.push((rate, tick));
                    true
                }
            };
            if due {
                for (recipient, &requested) in routes.iter().enumerate() {
                    if requested == rate {
                        selected |= 1 << recipient;
                    }
                }
            }
        }
        selected
    }
}

/// Compatibility entry point for native single-consumer tests/clients.
#[unsafe(no_mangle)]
pub extern "C" fn mqol_source_set_frame_rate(rate: u32) -> i32 {
    if rate > 240 {
        return crate::ERR_INVALID_ARGUMENT;
    }
    let mut routes = [NO_ROUTE; ROUTE_COUNT];
    routes[0] = rate;
    *ROUTES.lock().unwrap_or_else(|p| p.into_inner()) = (routes, 0);
    crate::OK
}

/// ABI 10: u32::MAX = inactive; zero = every presentation. One bit per route.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mqol_source_set_frame_routes(
    rates: *const u32,
    count: usize,
    version: u64,
) -> i32 {
    if rates.is_null() || count != ROUTE_COUNT {
        return crate::ERR_INVALID_ARGUMENT;
    }
    let input = unsafe { std::slice::from_raw_parts(rates, count) };
    if input.iter().any(|r| *r != NO_ROUTE && *r > 240) {
        return crate::ERR_INVALID_ARGUMENT;
    }
    let mut routes = [NO_ROUTE; ROUTE_COUNT];
    routes.copy_from_slice(input);
    *ROUTES.lock().unwrap_or_else(|p| p.into_inner()) = (routes, version);
    crate::OK
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn source_selects_each_rate_once_and_routes_the_same_image_to_equal_rate_sinks() {
        for input in [90, 120, 144, 165, 180, 240] {
            let mut gate = FrameCadence::default();
            let mut routes = [NO_ROUTE; ROUTE_COUNT];
            routes[..4].copy_from_slice(&[30, 60, 60, 0]);
            let mut counts = [0; 4];
            for i in 0..input * 10u64 {
                let mask = gate.select_routes(
                    1_700_000_000_001_000_000 + i * 1_000_000_000 / input,
                    &routes,
                );
                assert_eq!((mask >> 1) & 1, (mask >> 2) & 1);
                for (index, count) in counts.iter_mut().enumerate() {
                    *count += (mask >> index) & 1;
                }
            }
            assert!((300..=301).contains(&counts[0]), "{input} Hz: {counts:?}");
            assert!((600..=601).contains(&counts[1]), "{input} Hz: {counts:?}");
            assert_eq!(counts[3] as u64, input * 10);
        }
    }

    #[test]
    fn late_join_and_rate_changes_do_not_resample_other_routes() {
        let mut gate = FrameCadence::default();
        let mut routes = [NO_ROUTE; ROUTE_COUNT];
        routes[0] = 60;
        let mut counts = [0; 2];
        for i in 0..1200u64 {
            if i == 7 {
                routes[1] = 60;
            }
            if i == 700 {
                routes[1] = 30;
            }
            let mask =
                gate.select_routes(1_700_000_000_001_000_000 + i * 1_000_000_000 / 120, &routes);
            if (7..700).contains(&i) {
                assert_eq!(mask & 1, (mask >> 1) & 1);
            }
            for (index, count) in counts.iter_mut().enumerate() {
                *count += (mask >> index) & 1;
            }
        }
        assert!((600..=601).contains(&counts[0]));
        assert!((470..=474).contains(&counts[1]));
        routes.fill(NO_ROUTE);
        assert_eq!(gate.select_routes(u64::MAX, &routes), 0);
    }
}
