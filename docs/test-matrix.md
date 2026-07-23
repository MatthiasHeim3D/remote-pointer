# Test matrix

## Automated coverage through Phase 2

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
| Client DPI conversion | 100%, 125%, and 150% scale; negative physical coordinates; reverse conversion |
| Overlay coordinate mapping | Four boundaries and center at 2560×1440 |
| Monitor selection | Initial selection, refresh preservation, selected-display disconnection |
| Marker commands | Four corners, center, invalid custom coordinates, overlay state errors |

## Manual display matrix for Phases 2–5

| Scenario | Presenter | Receiver | Status |
| --- | ---: | ---: | --- |
| Single monitor | 100% DPI | 100% DPI | Ready for manual Phase 2 test |
| Mixed scaling | 125% DPI | 150% DPI | Ready for manual Phase 2 test |
| High scaling | 200% DPI | 100% DPI | Ready for manual Phase 2 test |
| Different resolutions | 1920×1080 | 2560×1440 | Ready for manual Phase 2 test |
| Ultrawide to standard | 3440×1440 | 1920×1080 | Pending Phase 3 |
| Portrait receiver | Landscape | Portrait | Ready for manual Phase 2 test |
| Secondary left of primary | Yes | Yes | Ready for manual Phase 2 test |
| Runtime resolution change | Yes | Yes | Ready for manual Phase 2 test |
| Monitor disconnected | Yes | Yes | Ready for manual Phase 2 test |

## Phase 2 manual procedure

1. Run `dotnet run --project src/RemotePointer.Client`.
2. Select each monitor in turn and show the receiver overlay.
3. Click through the overlay into normal applications and confirm they activate and receive input normally.
4. Confirm the overlay itself never receives focus and does not appear on the taskbar.
5. Trigger all four corner markers and the center marker; verify the dot is centered on the expected normalized position.
6. Repeat with mixed scaling, negative virtual-screen origins, and portrait orientation.
7. Change the selected monitor's resolution and confirm the overlay is repositioned and resized.
8. Disconnect the selected monitor and confirm the overlay disappears and the control window shows an error.

End-to-end latency, reconnect behavior, authorization, expiry, and rate-limit tests require the Phase 4 relay and Phase 5 client networking.
