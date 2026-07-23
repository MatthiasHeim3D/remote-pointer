# Test matrix

## Automated coverage through Phase 7

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
| Receiver discovery | Disabled-by-default capability, receiver opt-in, directory filtering, direct request, mandatory approval |
| Display synchronization | Approval sends dimensions, receiver changes push to presenter, aspect/local display changes invalidate calibration |
| Relay authorization | Receiver-only approval, presenter-only send, receiver-only acknowledgement |
| Session lifecycle | Creation, approval, active expiry, termination, presenter and receiver resume |
| Pointer defenses | TTL, sequence duplicate suppression, 20/s refill, burst of 30 |
| In-memory SignalR | Join/approve/send/acknowledge, both-role reconnect, termination, unauthorized sender |
| Relay hosting | Health endpoint, 8 KB message limit, single invocation per client |
| Receiver networking | Session creation, approval presentation, fresh marker acknowledgement, expired marker drop |
| Presenter networking | Approval gating, receiver dimensions, pointer construction, acknowledgement latency, reconnect drop |
| Client SignalR transport | Real two-client create/join/approve/send/acknowledge/terminate flow through in-memory relay |
| Production transport | Plaintext refusal by default, explicit private proxy mode, secure health response, HSTS |
| Protected recovery | At-rest token opacity, corruption/identity rejection, restart resume, token rotation, post-recovery pointer |
| Audit privacy | Structured client record excludes exception messages, credentials, and coordinate fields |
| Hub rate limiting | Real transport accepts burst of 30 and rejects immediate event 31 |
| Client configuration | Packaged and environment URLs both enforce HTTPS |

## Manual display matrix for Phases 2–5

| Scenario | Presenter | Receiver | Status |
| --- | ---: | ---: | --- |
| Single monitor | 100% DPI | 100% DPI | Ready for manual Phase 5 test |
| Mixed scaling | 125% DPI | 150% DPI | Ready for manual Phase 5 test |
| High scaling | 200% DPI | 100% DPI | Ready for manual Phase 5 test |
| Different resolutions | 1920×1080 | 2560×1440 | Ready for manual Phase 5 test |
| Ultrawide to standard | 3440×1440 | 1920×1080 | Ready for manual Phase 5 test |
| Portrait receiver | Landscape | Portrait | Ready for manual Phase 5 test |
| Secondary left of primary | Yes | Yes | Ready for manual Phase 5 test |
| Runtime resolution change | Yes | Yes | Ready for manual Phase 5 test |
| Monitor disconnected | Yes | Yes | Ready for manual Phase 5 test |

## Phase 2 manual procedure

1. Run `dotnet run --project src/RemotePointer.Client`.
2. Select each monitor in turn and show the receiver overlay.
3. Click through the overlay into normal applications and confirm they activate and receive input normally.
4. Confirm the overlay itself never receives focus and does not appear on the taskbar.
5. Trigger all four corner markers and the center marker; verify the dot is centered on the expected normalized position.
6. Repeat with mixed scaling, negative virtual-screen origins, and portrait orientation.
7. Change the selected monitor's resolution and confirm the overlay is repositioned and resized.
8. Disconnect the selected monitor and confirm the overlay disappears and the control window shows an error.

## Phase 3 manual procedure

1. Approve a presenter session, confirm the receiver dimensions appear automatically, and select **Calibrate target area**.
2. Move and resize the calibration window over a normal application. Verify dimensions and aspect-ratio difference update continuously.
3. Disable ratio lock, create a difference greater than 2%, and verify Lock requires the explicit Allow mismatch override.
4. Reset and lock the rectangle. Confirm the target window disappears and state changes to Ready.
5. Enable pointing with the button. Click inside the target and confirm a local ripple plus normalized coordinates in the control window.
6. Place the rectangle over a clickable test button and confirm an inside click does not activate the underlying button.
7. Click outside the rectangle and confirm the underlying application behaves normally.
8. Press Escape and confirm normal clicking is restored immediately.
9. Repeat entry and exit with `Ctrl+Alt+P`, including while another application is active.
10. Repeat on mixed-DPI monitors and with a target rectangle on a monitor left of the primary display.

## Phase 5 manual procedure

1. Trust the local ASP.NET Core development certificate and start the HTTPS relay.
2. Start two client processes. In the receiver, select a monitor and create a session.
3. Enter the displayed code in the presenter. Confirm the receiver shows the presenter's machine name and that calibration remains disabled until approval.
4. Approve the presenter, calibrate the shared desktop region, and enable pointing.
5. Click the four corners and center. Confirm equivalent receiver positions, a local ripple, and a displayed acknowledgement latency.
6. End the session from each role in separate runs. Confirm the receiver overlay disappears and presenter pointing exits immediately.
7. During an active session, interrupt relay connectivity and click while the UI shows Reconnecting. Confirm those clicks are reported as dropped and do not appear after reconnection.
8. Restore connectivity within the reconnect window. Confirm both roles resume and newly captured pointers work without re-pairing.
9. Minimize each client, confirm notification-area status, restore by double-clicking the icon, and exit from its menu.
10. On a representative corporate LAN, collect at least several hundred acknowledgement samples and verify p95 click-to-marker latency is below 250 ms.
11. Enable relay discovery, opt the receiver into visibility, refresh the presenter list, and request the receiver directly. Confirm the same receiver approval is required. Disable discovery on the relay and confirm both discovery controls are disabled while pairing-code joins still work.
12. While approved, change the receiver resolution and confirm the sender's displayed dimensions update. Change the receiver aspect ratio and confirm stale calibration is invalidated. Change the sender's local display configuration and confirm recalibration is required there as well.

## Phase 6 manual procedure

1. On distinct Windows user profiles or endpoints, establish and approve a session, then terminate one client from Task Manager while leaving the relay running.
2. Restart that client. Confirm it reports recovered/resumed state, rotates its reconnect token, and resumes receiver markers or presenter approval. Recalibrate the presenter because calibration geometry is intentionally not persisted.
3. End the recovered session and confirm the corresponding protected role file under `%LocalAppData%\RemotePointer\Sessions` is removed.
4. Inspect `%LocalAppData%\RemotePointer\Logs\audit-YYYYMMDD.jsonl`. Confirm records are valid JSON and contain no coordinates, pairing codes, session/reconnect tokens, exception messages, screen metadata, or typed data.
5. Start the Docker deployment. Confirm HTTPS health succeeds through Caddy, relay port 8080 is not reachable from the LAN, HSTS is present, and an untrusted Caddy root causes the client connection to fail.
6. Repeat the full Phase 5 display matrix as a standard user and confirm no administrator rights are required by the client.

## Phase 7 small-deployment procedure

1. Start Compose with the final DNS hostname and confirm `https://<hostname>/health` through Caddy.
2. Export only Caddy's `root.crt`; confirm its private key remains in the persistent Docker volume.
3. Build the Inno Setup package with the matching HTTPS URL and root, and archive the generated SHA-256 file on the restricted internal share.
4. Run `build\Test-Installer.ps1` as a non-administrator. Confirm current-user install and uninstall succeed.
5. Install normally and leave the HTTPS trust task selected. Confirm the Start menu shortcut is current-user and the health URL succeeds without certificate warnings.
6. Establish a real receiver/presenter session from two machines, including one over VPN, and repeat join approval, calibration, pointing, termination, and reconnect tests.
7. Confirm the clients require only outbound TCP 443 and relay port 8080 is not published by Docker.
