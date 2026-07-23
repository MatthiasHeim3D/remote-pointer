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
| Calibration geometry | Locked/unlocked resize, horizontal/vertical ratio preservation, minimum dimensions |
| Presenter state | Expected-ratio validation, calibration request, ready/pointing transitions, hotkey errors |
| Presenter capture reporting | Normalized local pointer count and coordinate presentation |
| Pairing and secrets | Friendly cryptographic codes, hashing, one-time consumption, expiry |
| Relay authorization | Receiver-only approval, presenter-only send, receiver-only acknowledgement |
| Session lifecycle | Creation, approval, active expiry, termination, presenter and receiver resume |
| Pointer defenses | TTL, sequence duplicate suppression, 20/s refill, burst of 30 |
| In-memory SignalR | Join/approve/send/acknowledge, both-role reconnect, termination, unauthorized sender |
| Relay hosting | Health endpoint, 8 KB message limit, single invocation per client |

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

## Phase 3 manual procedure

1. Open **Point at another screen**, enter the expected receiver dimensions, and select **Calibrate target area**.
2. Move and resize the calibration window over a normal application. Verify dimensions and aspect-ratio difference update continuously.
3. Disable ratio lock, create a difference greater than 2%, and verify Lock requires the explicit Allow mismatch override.
4. Reset and lock the rectangle. Confirm the target window disappears and state changes to Ready.
5. Enable pointing with the button. Click inside the target and confirm a local ripple plus normalized coordinates in the control window.
6. Place the rectangle over a clickable test button and confirm an inside click does not activate the underlying button.
7. Click outside the rectangle and confirm the underlying application behaves normally.
8. Press Escape and confirm normal clicking is restored immediately.
9. Repeat entry and exit with `Ctrl+Alt+P`, including while another application is active.
10. Repeat on mixed-DPI monitors and with a target rectangle on a monitor left of the primary display.
