# Architecture

## System boundary

Remote Pointer has three deployable or reusable components:

| Component | Responsibility | Must not do |
| --- | --- | --- |
| `RemotePointer.Client` | Annotator calibration/capture and host marker overlays | Capture screens or inject remote input |
| `RemotePointer.Server` | Validate, authorize, rate-limit, and relay transient pointer events | Access either user's desktop |
| `RemotePointer.Contracts` | Transport-neutral messages, coordinate math, JSON policy, and structural validation | Depend on WPF, SignalR, or ASP.NET Core |

Clients establish outbound HTTPS/WSS connections to the server. The server places the approved annotators and host in a session-specific SignalR group. Peer-to-peer connectivity is deliberately excluded.

## Phase 1 design

The contracts assembly targets `net10.0` and contains no desktop or web framework references. Coordinates use small immutable value types based on `double`, allowing later callers to keep WPF-DIP and physical-pixel conversion at the platform boundary.

`CoordinateMapper.Normalize` uses a calibrated rectangle that can have a negative origin. It clamps its result to the inclusive normalized range. `CoordinateMapper.Denormalize` requires an already valid normalized coordinate; malformed network input must be rejected rather than silently corrected.

Structural contract validation returns stable error codes and does not throw for untrusted messages. Session membership, role authorization, expiry, and rate limits require server state and remain server responsibilities for Phase 4. `SequenceNumberTracker` provides a bounded replay/reordering window that the server can maintain per approved annotator session.

## Phase 2 design

The host prototype enumerates monitor handles and physical bounds through `EnumDisplayMonitors` and `GetMonitorInfo`. Each monitor retains its native virtual-desktop origin, including negative coordinates. Effective monitor DPI is recorded as a scale factor, and `DisplayCoordinateMapper` is the only client service that converts physical pixels to WPF device-independent units.

`HostOverlayWindow` is a transparent top-level WPF window positioned with `SetWindowPos` in physical pixels. After handle creation it applies `WS_EX_LAYERED`, `WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE`, and `WS_EX_TOOLWINDOW`. Native hit testing returns `HTTRANSPARENT`, and mouse activation returns `MA_NOACTIVATE`.

Both the control window and host overlay listen for `WM_DISPLAYCHANGE`. Resolution or placement changes cause the selected display to be enumerated again and the overlay to be repositioned. If its display ID no longer exists, the overlay closes and the main window presents an explicit disconnection error.

Markers are local-only Phase 2 test visuals. Up to five expanding rings can be visible simultaneously, each fading after approximately 900 ms. Their centers are mapped from normalized coordinates into the overlay's current WPF client size.

## Phase 3 design

`TargetRegionService` owns the annotator state machine: inactive, calibrating, ready, and pointing. A calibrated rectangle is retained only in memory for the current process session. Recalibration can preserve the last rectangle, while Reset restores a recommended rectangle matching the host's expected aspect ratio.

`TargetRegionWindow` is interactive during calibration. Its header moves the window, a resize thumb changes its dimensions, and a ratio lock — checked when calibration opens — constrains resizing to the host's shape. Clearing the lock allows any rectangle; a resulting difference of more than 2% from the host's shape raises an aspect-ratio mismatch warning. The warning is advisory, and Start begins pointing with the rectangle as drawn.

Pointing mode reuses the exact calibrated top-level window bounds with transparent content and a subtle border. Because the pointer window exists only over the target rectangle, normal input outside it is unaffected. WPF distinguishes a left click, left-drag path, Shift+left-drag line, Shift+left-click text annotation, right-drag rectangle, and Shift+right-drag circle. Gestures are rendered locally, normalized through the shared `CoordinateMapper`, and never forwarded or injected into the underlying application. A dispatcher-driven send loop delivers updates at roughly 60 Hz. Freehand updates batch every mouse sample collected between frames, while a low-frequency keepalive preserves stationary gestures until button release.

The control window registers `Ctrl+Alt+P` with `RegisterHotKey`; no keyboard or mouse hook is installed. The target window accepts keyboard input only for Escape, the usage-help `H` shortcut, and an explicitly opened text annotation editor. Enter finalizes that editor into non-editable plain text. Inactive mode closes the target window entirely.

## Phase 4 design

`PointerHub` is a typed SignalR hub at `/hubs/pointer`. It contains orchestration only: every state transition and authorization decision is delegated to the singleton `SessionManager`. A SignalR group exists per approved session, while pointer and acknowledgement delivery is addressed directly to the validated host or originating annotator connection so no pending or unrelated client receives transient data.

`SessionManager` uses a single in-process synchronization boundary around its dictionaries. A session has one host and a host-configured collection of annotators, capped by the server at 16. SignalR connection IDs are current routes, not identities. Each annotator has independent credentials and sequence tracking. Pointer event IDs retain their originating annotator route so acknowledgements return only to the annotator that produced the event. Resume validates both token hashes, replaces a still-valid route, and rotates the reconnect token; once an annotator route disconnects, its membership and sequence state are revoked.

Session IDs, session secrets, role tokens, and reconnect tokens each use 256 bits of random input; only hashes are retained by the manager.

The host directory is the only way into a session: a direct request enters a pending-annotator state, so explicit host approval remains the sole credential-issuance boundary. Visibility is the host's own per-session choice — a new session publishes itself and can be hidden again at any time — rather than an operator switch, because a relay that refused to publish anything would accept no join at all and could serve nobody.

The directory is scoped by server password. A client presents a key derived from its password with `EnterRelayGroup`, and the relay records it against the connection and against any session that connection creates; listings, join requests and directory notifications are filtered to a matching key. The key is the group's whole identity, so groups need no registry and no cleanup — an unused one simply has no members, and a host's offline shell reattaches when its client returns with the same password. `Sessions:RequireServerPassword` defaults to true and refuses an empty key; setting it to false puts those clients in one shared open pool. Directory entries carry the host's chosen display name and optional profile picture, which is what makes the password the boundary that matters.

A changed password moves the connection between groups, and a published host moves with it, whether it presents the new key on its live connection or on the one it resumes with. Both directories are notified, because the group it left can no longer see it and the group it joined now can. A pending request that no longer shares the session's group is cancelled from either side, since approving one would form a session across the boundary the password draws. An annotator the host already approved keeps its place: the password scopes discovery, and a live connection ends when the host disconnects that annotator from its row or disconnects all of them.

Pointer acceptance applies strict JSON/contract validation, current role authorization, session expiry, a token bucket, and a bounded sequence window. Each approved annotator has its own bucket, created on approval and sized by `RateLimits:EventsPerSecond` and `RateLimits:BurstSize` — 90 events per second with a burst of 180 by default. The budget describes one annotator's stream: an annotator draws at roughly 60 Hz, so a shared session budget would throttle every participant as soon as two of them drew at the same time. Duplicate or significantly old sequence numbers are dropped. Events are delivered only when the host is currently connected and are never queued for replay.

A background cleanup service expires active sessions and collects unreachable ones. A session nobody has asked to join is collected once `Sessions:AbandonedSessionLifetimeMinutes` passes, but only while nothing can still reach it: a connected host that chose to be invisible can publish itself again and keeps its session, whereas a hidden, disconnected shell cannot be joined and is collected. The relay uses structured JSON console logging and records aggregate pointer counts only when a session ends or expires.

## Phase 5 design

`SignalRRelayClient` is the WPF-independent client transport boundary. It creates the connection lazily, uses the shared strict JSON policy, and reports explicit connection states. Host and annotator use separate connections while sharing one durable random client-instance ID stored under the current user's local application data. Phase 6 adds protected restart recovery without changing this live transport boundary.

The host view model exposes an **Available**/**Invisible** selector. Choosing **Available** for the first time creates and automatically publishes the host; later changes update the stored visibility preference. It exposes each pending machine name for explicit approval, lists connected annotators in a dedicated receiving view, and disables the local annotator role while receiving. Every listed annotator carries its own pause and disconnect buttons and lights an indicator while its pointer events are still arriving; the bulk **Pause all** and **Disconnect all** actions appear only once a second annotator makes repeating a per-row click tedious. A pause leaves the session and the annotator's calibration intact and only stops its events from being relayed, which the annotator's input area reports with a dimmed pause symbol. The user-configured annotator limit defaults to two and applies when the host session starts. Disconnecting annotators preserves the host's availability preference. Incoming events are validated against TTL again immediately before display. Acknowledgements are sent only when the overlay accepts the marker.

The annotator view model exposes visible-host requests and cannot calibrate or point until approval supplies the host dimensions. Host resolution/rotation changes are pushed through the relay; aspect changes invalidate stale calibration. Local display-configuration changes also require recalibration. Each captured event gets a new event ID and monotonic sequence number and is sent immediately; related drag events also share an ephemeral gesture ID. Events are never queued: a send during disconnection or reconnection returns a dropped status. Host acknowledgements are correlated in memory to show event-to-display latency.

SignalR automatic reconnect invokes `ResumeSession` with the role credential and single-use reconnect token, then replaces the credential with the rotated result. Failure to resume clears the local session and exits annotator pointing. An annotator disconnect revokes that annotator and updates the host immediately. A host disconnect revokes all approved and pending annotators; only an offline, undiscoverable host shell remains eligible for transport recovery, and every annotator must submit a new request after it returns. Graceful host shutdown and expiry remove the whole server session. The notification-area icon reports invisible/available host, connected annotators, or active pointing state; minimizing hides the control window until the icon is opened.

## Phase 6 design

The production relay normally rejects plaintext without redirecting it. The small Docker deployment explicitly enables private HTTP between Caddy and the relay, but publishes only Caddy's HTTPS port. HSTS, hidden detailed hub errors, and safe production exception handling remain enabled. Stable audit event IDs cover connection, creation, join, rejection, approval, resume, termination, expiry, plaintext refusal, validation failure, and unexpected hub faults.

The client persists only `SessionCredential` recovery documents. They are serialized, encrypted with Windows DPAPI `CurrentUser`, atomically replaced under `%LocalAppData%\RemotePointer\Sessions`, and discarded on normal shutdown or when corrupt, expired, wrong-role, or bound to a different client identity. Startup after an ungraceful interruption may resume an empty host shell and atomically protect the rotated token. Host display selection is reconstructed from server session state; annotator calibration geometry remains intentionally ephemeral.

Automatic startup recovery runs only when the Windows profile contains one saved role. If both role files exist—as in a two-process, same-profile local test—neither process automatically replaces the other process's SignalR route. Creating or joining a new session discards only that role's recovered credential before binding the new connection.

Client lifecycle and fault events are JSON Lines records under `%LocalAppData%\RemotePointer\Logs`. The fixed schema permits event, level, session ID, role, exception type, and numeric error code; it has no coordinate, credential, exception-message, or arbitrary payload field. WPF dispatcher, AppDomain, and unobserved-task boundaries record failures without exposing details to the user.

Release analyzers remain warnings-as-errors. The dependency inventory and vulnerability scan are documented.

## Dependency direction

```text
RemotePointer.Client ----+
                         +--> RemotePointer.Contracts
RemotePointer.Server ----+
```

No reference is permitted from contracts back to either application. Client and server tests reference only the corresponding production project and contracts as needed.

## Phase status

- Phase 1: implemented.
- Phase 2: implemented; manual mixed-DPI and display-disconnection verification is required on target hardware.
- Phase 3: implemented; click-consumption and mixed-DPI calibration require manual verification on target hardware.
- Phase 4: implemented and covered by in-memory SignalR integration tests.
- Phase 5: implemented; LAN p95 latency and the full mixed-DPI matrix require manual target-environment verification.
- Phase 6: implemented.
- Phase 7: implemented with a small-network deployment profile.

## Phase 7 deployment boundary

The client is published self-contained for `win-x64` and packaged with Inno Setup for the current user under `%LocalAppData%\Programs\Remote Pointer`. The installer does not contain a relay URL; it only optionally embeds Caddy's public root certificate. With the certificate task selected, setup trusts that root only for the current Windows account. DPAPI recovery data and audit records remain separate under `%LocalAppData%\RemotePointer`.

The packaged `appsettings.json` ships an empty relay URL; the user enters it on first launch, stored in `user-settings.json` under `%LocalAppData%\RemotePointer`, with a process environment override retained for development. `REMOTEPOINTER_DATA_DIRECTORY` redirects that whole per-user tree — preferences, durable client identity, protected credentials, calibrations, and logs — which is what lets `build\Start-Development.ps1` run several clients side by side as if they were separate users. It is read once at startup, and DPAPI is scoped to the Windows account rather than the path, so a redirected client protects and reads its own credentials normally. Both paths enforce HTTPS. In Docker, Caddy is the only published service and proxies to the non-root relay on the private Compose network. The relay's in-memory session boundary means process restart terminates active sessions.
