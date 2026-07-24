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

`TargetRegionWindow` is interactive during calibration. Its header moves the window, a resize thumb changes its dimensions, and an optional ratio lock constrains resizing. The current dimensions, ratio, and relative difference from the receiver shape are displayed continuously. Locking a difference over 2% requires an explicit Allow mismatch choice.

Pointing mode reuses the exact calibrated top-level window bounds with transparent content and a subtle border. Because the pointer window exists only over the target rectangle, normal input outside it is unaffected. WPF distinguishes a left click, left-drag path, Shift+left-drag line, Shift+left-click text annotation, right-drag rectangle, and Shift+right-drag circle. Gestures are rendered locally, normalized through the shared `CoordinateMapper`, and never forwarded or injected into the underlying application. A dispatcher-driven sender delivers updates at roughly 60 Hz. Freehand updates batch every mouse sample collected between frames, while a low-frequency keepalive preserves stationary gestures until button release.

The control window registers `Ctrl+Alt+P` with `RegisterHotKey`; no keyboard or mouse hook is installed. The target window accepts keyboard input only for Escape, the usage-help `H` shortcut, and an explicitly opened text annotation editor. Enter finalizes that editor into non-editable plain text. Inactive mode closes the target window entirely.

## Phase 4 design

`PointerHub` is a typed SignalR hub at `/hubs/pointer`. It contains orchestration only: every state transition and authorization decision is delegated to the singleton `SessionManager`. A SignalR group exists per approved session, while pointer and acknowledgement delivery is addressed directly to the validated receiver or originating presenter connection so no pending or unrelated client receives transient data.

`SessionManager` uses a single in-process synchronization boundary around its dictionaries. A session has one receiver and a receiver-configured collection of presenters, capped by the server at 16. SignalR connection IDs are current routes, not identities. Each presenter has independent credentials and sequence tracking. Pointer event IDs retain their originating presenter route so acknowledgements return only to the sender that produced the event. Resume validates both token hashes, replaces a still-valid route, and rotates the reconnect token; once a presenter route disconnects, its membership and sequence state are revoked.

Pairing codes use a six-character unambiguous cryptographic alphabet and are indexed only by SHA-256 hash. A successful join consumes the code. Session IDs, session secrets, role tokens, and reconnect tokens each use 256 bits of random input; only hashes are retained by the manager.

Receiver discovery is enabled by the default deployment configuration and can be disabled by the server operator. Receivers still opt in per active session. Directory entries contain only a receiver machine label and opaque session ID. Direct requests enter the same pending-presenter state as pairing-code requests, so explicit receiver approval remains the sole credential-issuance boundary.

Pointer acceptance applies strict JSON/contract validation, current role authorization, session expiry, a 30-event token-bucket burst with a 20-event-per-second refill, and a bounded sequence window. Duplicate or significantly old sequence numbers are dropped. Events are delivered only when the receiver is currently connected and are never queued for replay.

A hosted cleanup service expires unused pairing sessions and active sessions. The relay uses structured JSON console logging and records aggregate pointer counts only when a session ends or expires.

## Phase 5 design

`SignalRRelayClient` is the WPF-independent client transport boundary. It creates the connection lazily, uses the shared strict JSON policy, and reports explicit connection states. Receiver and presenter use separate connections while sharing one durable random client-instance ID stored under the current user's local application data. Phase 6 adds protected restart recovery without changing this live transport boundary.

The receiver view model exposes an **Available**/**Invisible** selector. Choosing **Available** for the first time creates and automatically publishes the receiver; later changes update the stored visibility preference. It exposes each pending machine name for explicit approval, lists connected senders in a dedicated receiving view, disables the local sender role while receiving, and offers a prominent **Disconnect all senders** action. The user-configured sender limit defaults to two and applies when the receiver session starts. Disconnecting presenters preserves the receiver's availability preference. Incoming events are validated against TTL again immediately before display. Acknowledgements are sent only when the overlay accepts the marker.

The presenter view model exposes visible-receiver requests and cannot calibrate or point until approval supplies the receiver dimensions. Pairing-code joins remain in the relay protocol for compatibility but are not exposed by the desktop client. Receiver resolution/rotation changes are pushed through the relay; aspect changes invalidate stale calibration. Local display-configuration changes also require recalibration. Each captured event gets a new event ID and monotonic sequence number and is sent immediately; related drag events also share an ephemeral gesture ID. Events are never queued: a send during disconnection or reconnection returns a dropped status. Receiver acknowledgements are correlated in memory to show event-to-display latency.

SignalR automatic reconnect invokes `ResumeSession` with the role credential and single-use reconnect token, then replaces the credential with the rotated result. Failure to resume clears the local session and exits presenter pointing. A presenter disconnect revokes that presenter and updates the receiver immediately. A receiver disconnect revokes all approved and pending presenters; only an offline, undiscoverable receiver shell remains eligible for transport recovery, and every sender must submit a new request after it returns. Graceful receiver shutdown and expiry remove the whole server session. The notification-area icon reports invisible/available receiver, connected presenters, or active pointing state; minimizing hides the control window until the icon is opened.

## Phase 6 design

The production relay normally rejects plaintext without redirecting it. The small Docker deployment explicitly enables private HTTP between Caddy and the relay, but publishes only Caddy's HTTPS port. HSTS, hidden detailed hub errors, and safe production exception handling remain enabled. Stable audit event IDs cover connection, creation, join, rejection, approval, resume, termination, expiry, plaintext refusal, validation failure, and unexpected hub faults.

The client persists only `SessionCredential` recovery documents. They are serialized, encrypted with Windows DPAPI `CurrentUser`, atomically replaced under `%LocalAppData%\RemotePointer\Sessions`, and discarded on normal shutdown or when corrupt, expired, wrong-role, or bound to a different client identity. Startup after an ungraceful interruption may resume an empty receiver shell and atomically protect the rotated token. Receiver display selection is reconstructed from server session state; presenter calibration geometry remains intentionally ephemeral.

Automatic startup recovery runs only when the Windows profile contains one saved role. If both role files exist—as in a two-process, same-profile local test—neither process automatically replaces the other process's SignalR route. Creating or joining a new session discards only that role's recovered credential before binding the new connection.

Client lifecycle and fault events are JSON Lines records under `%LocalAppData%\RemotePointer\Logs`. The fixed schema permits event, level, session ID, role, exception type, and numeric error code; it has no coordinate, credential, pairing-code, exception-message, or arbitrary payload field. WPF dispatcher, AppDomain, and unobserved-task boundaries record failures without exposing details to the user.

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

The client is published self-contained for `win-x64` and packaged with Inno Setup for the current user under `%LocalAppData%\Programs\Remote Pointer`. The installer embeds the chosen HTTPS relay URL and Caddy public root certificate. With the certificate task selected, setup trusts that root only for the current Windows account. DPAPI recovery data and audit records remain separate under `%LocalAppData%\RemotePointer`.

Packaged JSON provides the relay URL, with a process environment override retained for development. Both paths enforce HTTPS. In Docker, Caddy is the only published service and proxies to the non-root relay on the private Compose network. The relay's in-memory session boundary means process restart terminates active sessions.
