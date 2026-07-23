# Security

## Narrow data boundary

The protocol contains only display geometry, normalized pointer events, acknowledgements, and session metadata. The solution has no screen-capture, audio, input-injection, clipboard, file-transfer, process-inspection, UI Automation, or conferencing-integration API.

The shared library does not reference Windows desktop APIs. Client Win32 interop is restricted to monitor geometry, overlay styles, display-change messages, and global hotkey registration under `RemotePointer.Client/Native`.

## Phase 1 controls

- Strict JSON parsing rejects unknown fields and integer enum coercion.
- All normalized coordinates must be finite and within the inclusive unit interval.
- Pointer timestamps and TTLs are validated before an event can be considered for display.
- Event IDs and a bounded sequence-number window support replay rejection.
- Friendly pairing codes exclude visually ambiguous characters.
- Nullable analysis, .NET analyzers, and warnings-as-errors are enabled repository-wide.

## Phase 2 overlay controls

- The receiver overlay is non-activating and returns transparent native hit-test results.
- The overlay never observes mouse input and has no keyboard handlers.
- Win32 interop is isolated under `RemotePointer.Client/Native` and is limited to display enumeration, DPI inspection, window placement, and extended window styles.
- Test markers are generated from local normalized coordinates; no screen contents or application metadata are accessed.

## Phase 3 input controls

- Pointing uses a bounded top-level WPF window rather than a low-level mouse hook.
- Only left clicks delivered inside the calibrated rectangle are observed, handled, and converted to normalized coordinates.
- No mouse movement or click is injected locally or remotely.
- `Ctrl+Alt+P` uses `RegisterHotKey`; no keyboard hook is installed.
- Escape is handled only while the presenter target window is focused.
- Leaving pointing mode closes the capture window, restoring normal application behavior everywhere.

## Controls scheduled for later phases

- Production plaintext refusal rather than redirection.
- Organization PKI certificate and approved-interface configuration.
- Protected client-side storage for role and reconnect credentials.
- Durable audit sink, retention policy, and operational alerting.
- Optional tenant/group-restricted Microsoft Entra ID authentication.

No development certificate-bypass switch will be included in production builds.

## Phase 4 relay controls

- Pairing codes, session secrets, session tokens, and reconnect tokens are cryptographically generated; only hashes are retained in server state.
- A pairing code is one-time and does not become a durable session credential.
- Presenter credentials are issued only after explicit approval from the session's receiver connection.
- Role and session membership are revalidated for every pointer, acknowledgement, resume, and termination operation.
- Reconnect requires the client-instance ID, session token, role, and a single-use rotating reconnect token.
- Pointer events are rejected for invalid coordinates, stale TTL, future timestamps, wrong sessions, unauthorized roles, excessive rate, and duplicate/old sequence numbers.
- SignalR receive payloads are limited to 8 KB.
- Structured logs omit pairing secrets, role tokens, reconnect tokens, and individual pointer coordinates.
- Production uses HTTPS redirection; no certificate-validation bypass exists.

## Phase 5 client controls

- The client accepts HTTPS relay URLs, with plaintext HTTP limited to loopback development addresses.
- TLS certificate validation uses the platform default; there is no bypass callback or configuration switch.
- Role credentials remain in process memory. Only the random client-instance ID is persisted under the current user's local application data.
- Calibration geometry lasts only for the process session and is never sent to the relay.
- Pointer sends are dropped while disconnected or reconnecting and are not replayed after resume.
- The receiver repeats structural and TTL validation immediately before display and acknowledges only displayed markers.
- Termination or failed resume removes the overlay, exits pointing, and clears the in-memory session state.
