# Architecture

## System boundary

Remote Pointer has three deployable or reusable components:

| Component | Responsibility | Must not do |
| --- | --- | --- |
| `RemotePointer.Client` | Presenter calibration/capture and receiver marker overlays | Capture screens or inject remote input |
| `RemotePointer.Server` | Validate, authorize, rate-limit, and relay transient pointer events | Access either user's desktop |
| `RemotePointer.Contracts` | Transport-neutral messages, coordinate math, JSON policy, and structural validation | Depend on WPF, SignalR, or ASP.NET Core |

Clients establish outbound HTTPS/WSS connections to the server. The server places the approved presenters and receiver in a session-specific SignalR group. Peer-to-peer connectivity is deliberately excluded.

## Phase 1 design

The contracts assembly targets `net10.0` and contains no desktop or web framework references. Coordinates use small immutable value types based on `double`, allowing later callers to keep WPF-DIP and physical-pixel conversion at the platform boundary.

`CoordinateMapper.Normalize` uses a calibrated rectangle that can have a negative origin. It clamps its result to the inclusive normalized range. `CoordinateMapper.Denormalize` requires an already valid normalized coordinate; malformed network input must be rejected rather than silently corrected.

Structural contract validation returns stable error codes and does not throw for untrusted messages. Session membership, role authorization, expiry, and rate limits require server state and remain server responsibilities for Phase 4. `SequenceNumberTracker` provides a bounded replay/reordering window that the server can maintain per approved presenter session.

## Phase 2 design

The receiver prototype enumerates monitor handles and physical bounds through `EnumDisplayMonitors` and `GetMonitorInfo`. Each monitor retains its native virtual-desktop origin, including negative coordinates. Effective monitor DPI is recorded as a scale factor, and `DisplayCoordinateMapper` is the only client service that converts physical pixels to WPF device-independent units.

`ReceiverOverlayWindow` is a transparent top-level WPF window positioned with `SetWindowPos` in physical pixels. After handle creation it applies `WS_EX_LAYERED`, `WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE`, and `WS_EX_TOOLWINDOW`. Native hit testing returns `HTTRANSPARENT`, and mouse activation returns `MA_NOACTIVATE`.

Both the control window and receiver overlay listen for `WM_DISPLAYCHANGE`. Resolution or placement changes cause the selected display to be enumerated again and the overlay to be repositioned. If its display ID no longer exists, the overlay closes and the main window presents an explicit disconnection error.

Markers are local-only Phase 2 test visuals. Up to five expanding rings can be visible simultaneously, each fading after approximately 900 ms. Their centers are mapped from normalized coordinates into the overlay's current WPF client size.

## Phase 3 design

`TargetRegionService` owns the presenter state machine: inactive, calibrating, ready, and pointing. A calibrated rectangle is retained only in memory for the current process session. Recalibration can preserve the last rectangle, while Reset restores a recommended rectangle matching the receiver's expected aspect ratio.

`TargetRegionWindow` is interactive during calibration. Its header moves the window, a resize thumb changes its dimensions, and a ratio lock — checked when calibration opens — constrains resizing to the receiver's shape. Clearing the lock allows any rectangle; a resulting difference of more than 2% from the receiver's shape raises an aspect-ratio mismatch warning. The warning is advisory, and Start begins pointing with the rectangle as drawn.

Pointing mode reuses the exact calibrated top-level window bounds with transparent content and a subtle border. Because the pointer window exists only over the target rectangle, normal input outside it is unaffected. WPF distinguishes a left click, left-drag path, Shift+left-drag line, Shift+left-click text annotation, right-drag rectangle, and Shift+right-drag circle. Gestures are rendered locally, normalized through the shared `CoordinateMapper`, and never forwarded or injected into the underlying application. A dispatcher-driven sender delivers updates at roughly 60 Hz. Freehand updates batch every mouse sample collected between frames, while a low-frequency keepalive preserves stationary gestures until button release.

The control window registers `Ctrl+Alt+P` with `RegisterHotKey`; no keyboard or mouse hook is installed. The target window accepts keyboard input only for Escape, the usage-help `H` shortcut, and an explicitly opened text annotation editor. Enter finalizes that editor into non-editable plain text. Inactive mode closes the target window entirely.

## Phase 4 design

`PointerHub` is a typed SignalR hub at `/hubs/pointer`. It contains orchestration only: every state transition and authorization decision is delegated to the singleton `SessionManager`. A SignalR group exists per approved session, while pointer and acknowledgement delivery is addressed directly to the validated receiver or originating presenter connection so no pending or unrelated client receives transient data.

`SessionManager` uses a single in-process synchronization boundary around its dictionaries. A session has one receiver and a receiver-configured collection of presenters, capped by the server at 16. SignalR connection IDs are current routes, not identities. Each presenter has independent credentials and sequence tracking. Pointer event IDs retain their originating presenter route so acknowledgements return only to the sender that produced the event. Resume validates both token hashes, replaces a still-valid route, and rotates the reconnect token; once a presenter route disconnects, its membership and sequence state are revoked.

Session IDs, session secrets, role tokens, and reconnect tokens each use 256 bits of random input; only hashes are retained by the manager.

Receiver discovery is controlled by the operator through `Sessions:ReceiverDiscoveryEnabled`. The relay image enables it unless the deployment overrides it, and the shipped Compose environment file turns it off. It is also the only way into a session: a direct request enters a pending-presenter state, so explicit receiver approval remains the sole credential-issuance boundary. Receivers still opt in per active session, and a relay with discovery switched off accepts no join at all.

The directory is scoped by server password. A client presents a key derived from its password with `EnterRelayGroup`, and the relay records it against the connection and against any session that connection creates; listings, join requests and directory notifications are filtered to a matching key. The key is the group's whole identity, so groups need no registry and no cleanup — an unused one simply has no members, and a receiver's offline shell reattaches when its client returns with the same password. `Sessions:RequireServerPassword` defaults to true and refuses an empty key; setting it to false puts those clients in one shared open pool. Directory entries carry the receiver's chosen display name and optional profile picture, which is what makes the password the boundary that matters.

Pointer acceptance applies strict JSON/contract validation, current role authorization, session expiry, a token bucket, and a bounded sequence window. Each approved presenter has its own bucket, created on approval and sized by `RateLimits:EventsPerSecond` and `RateLimits:BurstSize` — 90 events per second with a burst of 180 by default. The budget describes one sender's stream: a presenter draws at roughly 60 Hz, so a shared session budget would throttle every participant as soon as two of them drew at the same time. Duplicate or significantly old sequence numbers are dropped. Events are delivered only when the receiver is currently connected and are never queued for replay.

A hosted cleanup service expires active sessions and collects unreachable ones. A session nobody has asked to join is collected once `Sessions:AbandonedSessionLifetimeMinutes` passes, but only while nothing can still reach it: a connected receiver that chose to be invisible can publish itself again and keeps its session, whereas a disconnected shell — or any session on a relay with discovery switched off — cannot be joined and is collected. The relay uses structured JSON console logging and records aggregate pointer counts only when a session ends or expires.

## Phase 5 design

`SignalRRelayClient` is the WPF-independent client transport boundary. It creates the connection lazily, uses the shared strict JSON policy, and reports explicit connection states. Receiver and presenter use separate connections while sharing one durable random client-instance ID stored under the current user's local application data. Phase 6 adds protected restart recovery without changing this live transport boundary.

The receiver view model exposes an **Available**/**Invisible** selector. Choosing **Available** for the first time creates and automatically publishes the receiver; later changes update the stored visibility preference. It exposes each pending machine name for explicit approval, lists connected senders in a dedicated receiving view, disables the local sender role while receiving, and offers a prominent **Disconnect all senders** action. The user-configured sender limit defaults to two and applies when the receiver session starts. Disconnecting presenters preserves the receiver's availability preference. Incoming events are validated against TTL again immediately before display. Acknowledgements are sent only when the overlay accepts the marker.

The presenter view model exposes visible-receiver requests and cannot calibrate or point until approval supplies the receiver dimensions. Receiver resolution/rotation changes are pushed through the relay; aspect changes invalidate stale calibration. Local display-configuration changes also require recalibration. Each captured event gets a new event ID and monotonic sequence number and is sent immediately; related drag events also share an ephemeral gesture ID. Events are never queued: a send during disconnection or reconnection returns a dropped status. Receiver acknowledgements are correlated in memory to show event-to-display latency.

SignalR automatic reconnect invokes `ResumeSession` with the role credential and single-use reconnect token, then replaces the credential with the rotated result. Failure to resume clears the local session and exits presenter pointing. A presenter disconnect revokes that presenter and updates the receiver immediately. A receiver disconnect revokes all approved and pending presenters; only an offline, undiscoverable receiver shell remains eligible for transport recovery, and every sender must submit a new request after it returns. Graceful receiver shutdown and expiry remove the whole server session. The notification-area icon reports invisible/available receiver, connected presenters, or active pointing state; minimizing hides the control window until the icon is opened.

## Phase 6 design

The production relay normally rejects plaintext without redirecting it. The small Docker deployment explicitly enables private HTTP between Caddy and the relay, but publishes only Caddy's HTTPS port. HSTS, hidden detailed hub errors, and safe production exception handling remain enabled. Stable audit event IDs cover connection, creation, join, rejection, approval, resume, termination, expiry, plaintext refusal, validation failure, and unexpected hub faults.

The client persists only `SessionCredential` recovery documents. They are serialized, encrypted with Windows DPAPI `CurrentUser`, atomically replaced under `%LocalAppData%\RemotePointer\Sessions`, and discarded on normal shutdown or when corrupt, expired, wrong-role, or bound to a different client identity. Startup after an ungraceful interruption may resume an empty receiver shell and atomically protect the rotated token. Receiver display selection is reconstructed from server session state; presenter calibration geometry remains intentionally ephemeral.

Automatic startup recovery runs only when the Windows profile contains one saved role. If both role files exist—as in a two-process, same-profile local test—neither process automatically replaces the other process's SignalR route. Creating or joining a new session discards only that role's recovered credential before binding the new connection.

Client lifecycle and fault events are JSON Lines records under `%LocalAppData%\RemotePointer\Logs`. The fixed schema permits event, level, session ID, role, exception type, and numeric error code; it has no coordinate, credential, exception-message, or arbitrary payload field. WPF dispatcher, AppDomain, and unobserved-task boundaries record failures without exposing details to the user.

Release analyzers remain warnings-as-errors. The dependency inventory and vulnerability scan are documented.

## Dependency direction

```text
RemotePointer.Client ----+
                         +--> RemotePointer.Contracts
RemotePointer.Server ----+
```

No reference is permitted from contracts back to either host. Client and server tests reference only the corresponding production project and contracts as needed.

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

The packaged `appsettings.json` ships an empty relay URL; the user enters it on first launch, stored in `user-settings.json` under `%LocalAppData%\RemotePointer`, with a process environment override retained for development. Both paths enforce HTTPS. In Docker, Caddy is the only published service and proxies to the non-root relay on the private Compose network. The relay's in-memory session boundary means process restart terminates active sessions.
