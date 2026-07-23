# Test matrix

## Automated Phase 1 coverage

| Area | Cases |
| --- | --- |
| Normalization | Four exact corners, center, different sizes, negative origins, clamping |
| Denormalization | Negative-origin rectangle and invalid normalized input |
| Numeric safety | NaN, positive/negative infinity, zero/negative dimensions |
| Aspect ratio | Landscape, portrait, ultrawide, exact and over-2% tolerance |
| Pointer validation | Inclusive boundaries, stale TTL, TTL boundary, future timestamps, identity, sequence, kind |
| Display/join validation | Dimensions, DPI scale, rotation, pairing-code formatting, required identity/version |
| Sequence handling | Increasing values, duplicates, bounded reordering, significantly old values |
| JSON policy | camelCase, string enums, integer enum rejection, unknown-member rejection |

## Manual display matrix for Phases 2–5

| Scenario | Presenter | Receiver | Status |
| --- | ---: | ---: | --- |
| Single monitor | 100% DPI | 100% DPI | Pending Phase 2 |
| Mixed scaling | 125% DPI | 150% DPI | Pending Phase 2 |
| High scaling | 200% DPI | 100% DPI | Pending Phase 2 |
| Different resolutions | 1920×1080 | 2560×1440 | Pending Phase 2 |
| Ultrawide to standard | 3440×1440 | 1920×1080 | Pending Phase 3 |
| Portrait receiver | Landscape | Portrait | Pending Phase 2 |
| Secondary left of primary | Yes | Yes | Pending Phase 2 |
| Runtime resolution change | Yes | Yes | Pending Phase 2 |
| Monitor disconnected | Yes | Yes | Pending Phase 2 |

End-to-end latency, reconnect behavior, authorization, expiry, and rate-limit tests require the Phase 4 relay and Phase 5 client networking.
