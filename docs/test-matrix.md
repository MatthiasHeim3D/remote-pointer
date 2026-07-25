# Test matrix

## Automated coverage through Phase 7

| Area | Cases |
| --- | --- |
| Normalization | Four exact corners, center, different sizes, negative origins, clamping |
| Denormalization | Negative-origin rectangle and invalid normalized input |
| Numeric safety | NaN, positive/negative infinity, zero/negative dimensions |
| Aspect ratio | Landscape, portrait, ultrawide, exact and over-2% tolerance |
| Pointer validation | Inclusive boundaries, stale TTL, TTL boundary, future timestamps, identity, sequence, gesture identity, bounded text, kind |
| Display/join validation | Dimensions, DPI scale, rotation, required identity/version |
| Sequence handling | Increasing values, duplicates, bounded reordering, significantly old values |
| JSON policy | camelCase, string enums, integer enum rejection, unknown-member rejection |
| Client DPI conversion | 100%, 125%, and 150% scale; negative physical coordinates; reverse conversion |
| Overlay coordinate mapping | Four boundaries and center at 2560×1440 |
| Monitor selection | Initial selection, refresh preservation, selected-display disconnection |
| Marker commands | Four corners, center, invalid custom coordinates, overlay state errors |
| Calibration geometry | Locked/unlocked resize, horizontal/vertical ratio preservation, minimum dimensions, fullscreen monitor fitting |
| Presenter state | Expected-ratio validation, calibration request, ready/pointing transitions, hotkey errors |
| Presenter capture reporting | Normalized local pointer count and coordinate presentation |
| Session secrets and lifetime | Cryptographic generation, hashing, constant-time comparison, abandoned-session collection, expiry |
| Receiver directory | Receiver visibility choice, directory filtering, direct request, mandatory approval |
| Server password | Stable key derivation, minimum length, protected round trip, corrupt-file discard, group-scoped listing and joins, enforced and open relay modes, client warning states, settings entry states for change/apply/cancel |
| Display synchronization | Approval sends dimensions, receiver changes push to presenter, aspect/local display changes invalidate calibration |
| Relay authorization | Receiver-only approval, presenter-only send, receiver-only acknowledgement |
| Session lifecycle | Creation, approval, active expiry, termination, disconnect revocation, empty receiver-shell resume |
| Pointer defenses | TTL, sequence duplicate suppression, implausible forward-jump rejection, configurable token refill/burst metered per sender, production defaults of 90/s and 180 |
| In-memory SignalR | Join/approve/send/acknowledge, peer revocation on disconnect, fresh-request enforcement, termination, unauthorized sender |
| Relay hosting | Health endpoint, 32 KB message limit, single invocation per client |
| Receiver networking | Session creation, approval presentation, fresh marker acknowledgement, expired marker drop |
| Presenter networking | Approval gating, receiver dimensions, pointer construction, acknowledgement latency, reconnect drop |
| Client SignalR transport | Real two-client create/join/approve/send/acknowledge/terminate flow through in-memory relay |
| Production transport | Plaintext refusal by default, explicit private proxy mode, secure health response, HSTS |
| Protected recovery | At-rest token opacity, corruption/identity rejection, graceful-shutdown deletion, empty receiver recovery, fresh approval requirement |
| Audit privacy | Structured client record excludes exception messages, credentials, and coordinate fields |
| Hub rate limiting | Real transport accepts burst of 30 and rejects immediate event 31; a second sender keeps its own budget |
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
2. Move and resize the calibration window over a normal application. Verify dimensions and aspect-ratio difference update continuously, and that ratio-locked resizing remains stable without flickering.
3. Move it onto each sender monitor and select **Fullscreen**. With ratio lock enabled, confirm the largest receiver-shaped rectangle is centered within that monitor. Disable ratio lock and confirm it fills the complete monitor.
4. With ratio lock disabled, create a difference greater than 2%, and verify Lock requires the explicit Allow mismatch override.
5. Reset and lock the rectangle. Confirm the target window disappears and state changes to Ready.
6. Enable pointing. Verify left-click highlights, left-drag draws a fading path, Shift+left-drag draws a fading line, Shift+left-click opens a text box finalized by Enter, right-drag draws a fading box, and Shift+right-drag draws a fading circle centered at the initial click. Confirm each appears locally and on the receiver. Verify the help panel includes the Escape shortcut and settings guidance, opens on first use, and starts collapsed on later uses. Confirm `H` always toggles the full panel, while disabling **Show usage hints** removes the collapsed help badge.
7. With pointing active, change **Drawing opacity** in Settings and re-enter pointing. Confirm the sender's own shapes, ripples, and placed text notes are dimmed by the chosen percentage, that the receiver still renders them at full opacity, and that the value survives a client restart.
8. Place the rectangle over a clickable test button and confirm an inside click does not activate the underlying button.
9. Click outside the rectangle and confirm the underlying application behaves normally.
10. Press Escape and confirm normal clicking is restored immediately.
11. Repeat entry and exit with `Ctrl+Alt+P`, including while another application is active.
12. Repeat on mixed-DPI monitors and with a target rectangle on a monitor left of the primary display.

## Phase 5 manual procedure

1. Trust the local ASP.NET Core development certificate and start the HTTPS relay.
2. Start two client processes. In the receiver, select a monitor and choose **Available**. Confirm it appears in the presenter's receiver list automatically.
3. Select the receiver and request access. Confirm the receiver shows the presenter's machine name and that calibration remains disabled until approval.
4. Approve the presenter, calibrate the shared desktop region, and enable pointing.
5. Click the four corners and center. Confirm equivalent receiver positions, a local ripple, and a displayed acknowledgement latency.
6. Use **Disconnect all connections** on the receiver and confirm presenter pointing exits while the receiver returns to the available list. Request and approve access again, disconnect from the presenter, then choose **Invisible** on the receiver and confirm it no longer appears in the list.
7. During an active session, interrupt relay connectivity and click while the UI shows Reconnecting. Confirm those clicks are reported as dropped and do not appear after reconnection.
8. Restore connectivity within the reconnect window. Confirm the receiver can return, the previous sender is no longer connected, and pointing remains unavailable until that sender submits a new request and is approved again.
9. Minimize each client, confirm notification-area status, restore by double-clicking the icon, and exit from its menu.
10. On a representative corporate LAN, collect at least several hundred acknowledgement samples and verify p95 click-to-marker latency is below 250 ms.
11. Set the receiver to **Invisible** and confirm it leaves the directory on the other client and cannot be joined. Set it back to **Available** and confirm the visible-receiver flow works again.
12. While approved, change the receiver resolution and confirm the sender's displayed dimensions update. Change the receiver aspect ratio and confirm stale calibration is invalidated. Change the sender's local display configuration and confirm recalibration is required there as well.

## Phase 6 manual procedure

1. On distinct Windows user profiles or endpoints, establish and approve a session, then terminate one client from Task Manager while leaving the relay running.
2. Restart that client. After an ungraceful receiver exit, confirm it recovers with zero connected senders and the former sender must request approval again. After a normal exit, confirm no previous session is recovered.
3. Confirm the corresponding protected role file under `%LocalAppData%\RemotePointer\Sessions` is removed on normal shutdown.
4. Inspect `%LocalAppData%\RemotePointer\Logs\audit-YYYYMMDD.jsonl`. Confirm records are valid JSON and contain no coordinates, session/reconnect tokens, exception messages, screen metadata, or typed data.
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
