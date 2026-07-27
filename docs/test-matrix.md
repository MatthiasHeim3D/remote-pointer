# Test matrix

## Automated coverage through Phase 7

| Area | Cases |
| --- | --- |
| Normalization | Four exact corners, center, different sizes, negative origins, clamping |
| Denormalization | Negative-origin rectangle and invalid normalized input |
| Numeric safety | NaN, positive/negative infinity, zero/negative dimensions |
| Aspect ratio | Landscape, portrait, ultrawide, exact and over-2% tolerance |
| Pointer validation | Inclusive boundaries, stale TTL, TTL boundary, future timestamps, identity, sequence, gesture identity, bounded text, kind, annotation colour format |
| Color | Canonical form accepted, case/whitespace normalized, malformed and alpha-bearing values fall back to the default, channel parsing and darkening, preset canonicality and distinctness, presets cover every allocatable palette entry, selection ring across presets and custom, settings round trip, applied on selection rather than on save, colour carried on every sent pointer event |
| Color allocation | Distinct preferences untouched, earlier annotator keeps a contested colour, custom colour honoured until contested, displaced annotator restored when the holder leaves, no churn when nothing moved, caller always answered even when unmoved, one distinct preset each up to palette capacity, even repeats beyond it, malformed preferences read as the default, empty and vanished sessions harmless, approved-annotator-only, full relay round trip through the hub |
| Display/join validation | Dimensions, DPI scale, rotation, required identity/version |
| Sequence handling | Increasing values, duplicates, bounded reordering, significantly old values |
| JSON policy | camelCase, string enums, integer enum rejection, unknown-member rejection |
| Client DPI conversion | 100%, 125%, and 150% scale; negative physical coordinates; reverse conversion |
| Overlay coordinate mapping | Four boundaries and center at 2560×1440 |
| Monitor selection | Initial selection, refresh preservation, selected-display disconnection |
| Marker commands | Four corners, center, invalid custom coordinates, overlay state errors |
| Calibration geometry | Locked/unlocked resize, horizontal/vertical ratio preservation, minimum dimensions, fullscreen monitor fitting |
| Annotator state | Expected-ratio validation, calibration request, ready/annotating transitions, hotkey errors |
| Annotator capture reporting | Normalized local pointer count and coordinate presentation |
| Session secrets and lifetime | Cryptographic generation, hashing, constant-time comparison, abandoned-session collection, expiry |
| Host directory | Host visibility choice, directory filtering, direct request, mandatory approval |
| Server password | Stable key derivation, minimum length, constant-time key match, protected round trip, corrupt-file discard, connections refused at negotiate without or with a wrong password, open relay admitting a client that has none, client warning states, settings entry states for change/apply/cancel |
| Rooms | Name normalisation and default fallback, room-scoped listing and joins, case and space folding across clients, host moved out of the room it left, join request cancelled across a room change, approved annotator kept, persistence as typed and delivery to both relay connections |
| Display synchronization | Approval sends dimensions, host changes push to annotator, aspect/local display changes invalidate calibration |
| Relay authorization | Host-only approval, annotator-only send, host-only acknowledgement, host-only pause and per-annotator disconnect |
| Annotator pause | Paused events dropped rather than relayed, pause-all across annotators, resume restores relaying, pause survives into resumed session state, annotator input area blocked while paused |
| Session lifecycle | Creation, approval, active expiry, termination, disconnect revocation, empty host-shell resume |
| Pointer defenses | TTL, sequence duplicate suppression, implausible forward-jump rejection, configurable token refill/burst metered per annotator, production defaults of 90/s and 180 |
| In-memory SignalR | Join/approve/send/acknowledge, peer revocation on disconnect, fresh-request enforcement, termination, unauthorized annotator |
| Relay endpoints | Health endpoint, 32 KB message limit, single invocation per client |
| Host networking | Session creation, approval presentation, fresh marker acknowledgement, expired marker drop |
| Annotator networking | Approval gating, host dimensions, pointer construction, acknowledgement latency, reconnect drop |
| Client SignalR transport | Real two-client create/join/approve/send/acknowledge/terminate flow through in-memory relay |
| Production transport | Plaintext refusal by default, explicit private proxy mode, secure health response, HSTS |
| Protected recovery | At-rest token opacity, corruption/identity rejection, graceful-shutdown deletion, empty host recovery, fresh approval requirement |
| Audit privacy | Structured client record excludes exception messages, credentials, and coordinate fields |
| Hub rate limiting | Real transport accepts burst of 30 and rejects immediate event 31; a second annotator keeps its own budget |
| Client configuration | Packaged and environment URLs both enforce HTTPS |

## Manual display matrix for Phases 2–5

| Scenario | Annotator | Host | Status |
| --- | ---: | ---: | --- |
| Single monitor | 100% DPI | 100% DPI | Ready for manual Phase 5 test |
| Mixed scaling | 125% DPI | 150% DPI | Ready for manual Phase 5 test |
| High scaling | 200% DPI | 100% DPI | Ready for manual Phase 5 test |
| Different resolutions | 1920×1080 | 2560×1440 | Ready for manual Phase 5 test |
| Ultrawide to standard | 3440×1440 | 1920×1080 | Ready for manual Phase 5 test |
| Portrait host | Landscape | Portrait | Ready for manual Phase 5 test |
| Secondary left of primary | Yes | Yes | Ready for manual Phase 5 test |
| Runtime resolution change | Yes | Yes | Ready for manual Phase 5 test |
| Monitor disconnected | Yes | Yes | Ready for manual Phase 5 test |

## Phase 2 manual procedure

1. Run `dotnet run --project src/RemotePointer.Client`.
2. Select each monitor in turn and show the host overlay.
3. Click through the overlay into normal applications and confirm they activate and receive input normally.
4. Confirm the overlay itself never receives focus and does not appear on the taskbar.
5. Trigger all four corner markers and the center marker; verify the dot is centered on the expected normalized position.
6. Repeat with mixed scaling, negative virtual-screen origins, and portrait orientation.
7. Change the selected monitor's resolution and confirm the overlay is repositioned and resized.
8. Disconnect the selected monitor and confirm the overlay disappears and the control window shows an error.

## Phase 3 manual procedure

1. Approve an annotator session, confirm the host dimensions appear automatically, and select **Calibrate target area**.
2. Move and resize the calibration window over a normal application. Verify dimensions and aspect-ratio difference update continuously, and that ratio-locked resizing remains stable without flickering.
3. Move it onto each annotator monitor and select **Fullscreen**. With ratio lock enabled, confirm the largest host-shaped rectangle is centered within that monitor. Disable ratio lock and confirm it fills the complete monitor.
4. With ratio lock disabled, create a difference greater than 2%, and verify Lock requires the explicit Allow mismatch override.
5. Reset and lock the rectangle. Confirm the target window disappears and state changes to Ready.
6. Enable annotating. Verify left-click highlights, left-drag draws a fading path, Shift+left-drag draws a fading line, Shift+left-click opens a text box finalized by Enter, right-drag draws a fading box, and Shift+right-drag draws a fading circle centered at the initial click. Confirm each appears locally and on the host. Verify the help panel includes the Escape shortcut and settings guidance, opens on first use, and starts collapsed on later uses. Confirm `H` always toggles the full panel, while disabling **Show usage hints** removes the collapsed help badge.
7. With annotating active, change **Drawing opacity** in Settings and re-enter annotating. Confirm the annotator's own shapes, ripples, and placed text notes are dimmed by the chosen percentage, that the host still renders them at full opacity, and that the value survives a client restart.
8. With annotating still active, pick each preset under **Annotator Color**. Confirm each takes effect on the next stroke without closing Settings or recalibrating, that ripples, shapes, freehand ink, and placed text notes all take it, and that the host draws the same colour. Confirm strokes already on screen keep the colour they were drawn in. Confirm the target-area frame and the box text is typed into stay the standard red at every colour. Open the custom picker, choose a colour that is not a preset, and confirm the selection ring moves to the custom swatch and the colour survives a client restart. With several annotators connected to one host, give each a different colour and confirm the host tells their simultaneous drawings apart, including after one of them changes colour mid-session.
9. Give a second annotator a colour the first already holds. Confirm the first is not disturbed, the second is moved to a different preset on both its own screen and the host's, and its Settings pane still shows the swatch it picked with a note naming the colour in use. Disconnect the first and confirm the second returns to its own colour unprompted. Repeat with eight annotators and confirm every preset is in use before any colour repeats.
10. Place the rectangle over a clickable test button and confirm an inside click does not activate the underlying button.
11. Click outside the rectangle and confirm the underlying application behaves normally.
12. Press Escape and confirm normal clicking is restored immediately.
13. Repeat entry and exit with `Ctrl+Alt+P`, including while another application is active.
14. Repeat on mixed-DPI monitors and with a target rectangle on a monitor left of the primary display.

## Phase 5 manual procedure

1. Trust the local ASP.NET Core development certificate and start the HTTPS relay.
2. Start two client processes. In the host, select a monitor and choose **Available**. Confirm it appears in the annotator's host list automatically.
3. Select the host and request access. Confirm the host shows the annotator's machine name and that calibration remains disabled until approval.
4. Approve the annotator, calibrate the shared desktop region, and enable annotating.
5. Click the four corners and center. Confirm equivalent host positions, a local ripple, and a displayed acknowledgement latency.
6. Pause the annotator from its row on the host. Confirm the annotator's input area dims to a pause symbol, its clicks and drags produce nothing on either side, and its row reads **Paused**. Resume it and confirm annotating works again and the annotating indicator lights while it draws. Then disconnect it from its row and confirm annotator annotating exits while the host returns to the available list.
7. With two annotators approved, confirm **Pause all** and **Disconnect all** appear, pause both, and confirm the button offers **Resume all**.
8. Use **Disconnect all** on the host and confirm annotator annotating exits while the host returns to the available list. Request and approve access again, disconnect from the annotator, then choose **Invisible** on the host and confirm it no longer appears in the list.
9. During an active session, interrupt relay connectivity and click while the UI shows Reconnecting. Confirm those clicks are reported as dropped and do not appear after reconnection.
10. Restore connectivity within the reconnect window. Confirm the host can return, the previous annotator is no longer connected, and annotating remains unavailable until that annotator submits a new request and is approved again.
11. Minimize each client, confirm notification-area status, restore by double-clicking the icon, and exit from its menu.
12. On a representative corporate LAN, collect at least several hundred acknowledgement samples and verify p95 click-to-marker latency is below 250 ms.
13. Set the host to **Invisible** and confirm it leaves the directory on the other client and cannot be joined. Set it back to **Available** and confirm the visible-host flow works again.
14. While approved, change the host resolution and confirm the annotator's displayed dimensions update. Change the host aspect ratio and confirm stale calibration is invalidated. Change the annotator's local display configuration and confirm recalibration is required there as well.

## Phase 6 manual procedure

1. On distinct Windows user profiles or endpoints, establish and approve a session, then terminate one client from Task Manager while leaving the relay running.
2. Restart that client. After an ungraceful host exit, confirm it recovers with zero connected annotators and the former annotator must request approval again. After a normal exit, confirm no previous session is recovered.
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
6. Establish a real host/annotator session from two machines, including one over VPN, and repeat join approval, calibration, annotating, termination, and reconnect tests.
7. Confirm the clients require only outbound TCP 443 and relay port 8080 is not published by Docker.
