# Security

## Narrow data boundary

The protocol contains only display geometry, normalized pointer events, deliberate transient text annotations, acknowledgements, and session metadata. The solution has no screen-capture, audio, input-injection, clipboard, file-transfer, process-inspection, UI Automation, or conferencing-integration API.

The shared library does not reference Windows desktop APIs. Client Win32 interop is restricted to monitor geometry, overlay styles, display-change messages, and global hotkey registration under `RemotePointer.Client/Native`.

## Phase 1 controls

- Strict JSON parsing rejects unknown fields and integer enum coercion.
- All normalized coordinates must be finite and within the inclusive unit interval.
- Pointer timestamps and TTLs are validated before an event can be considered for display.
- Event IDs and a bounded sequence-number window support replay rejection.
- Nullable analysis, .NET analyzers, and warnings-as-errors are enabled repository-wide.

## Phase 2 overlay controls

- The host overlay is non-activating and returns transparent native hit-test results.
- The overlay never observes mouse input and has no keyboard handlers.
- Win32 interop is isolated under `RemotePointer.Client/Native` and is limited to display enumeration, DPI inspection, window placement, and extended window styles.
- Test markers are generated from local normalized coordinates; no screen contents or application metadata are accessed.

## Phase 3 input controls

- Annotating uses a bounded top-level WPF window rather than a low-level mouse hook.
- Only explicit pointer gestures delivered inside the calibrated rectangle are observed and converted to normalized coordinates while annotating mode is active.
- Keyboard text is observed only after Shift+left-click opens the visible annotation editor; Enter finalizes at most 256 plain-text characters. Text is transient, is not interpreted as markup, and is never persisted or logged.
- No mouse movement or click is injected locally or remotely.
- `Ctrl+Alt+P` uses `RegisterHotKey`; no keyboard hook is installed.
- Escape is handled only while the annotator target window is focused.
- Leaving annotating mode closes the capture window, restoring normal application behavior everywhere.

## Controls scheduled for later phases

- Durable audit sink, retention policy, and operational alerting.
- Optional tenant/group-restricted Microsoft Entra ID authentication.
- Organization-specific PKI issuance, certificate renewal, and approved-interface policy.

No development certificate-bypass switch will be included in production builds.

## Phase 4 relay controls

- Session secrets, session tokens, and reconnect tokens are cryptographically generated; only hashes are retained in server state.
- A host controls its own visibility for each active session and can hide it at any time. The directory is the only route into a session, so there is no operator switch to disable it — a relay that published nothing could serve nobody.
- A server password scopes the directory. The client derives a key from it with PBKDF2-SHA256 and the relay never receives the password, so listings, join requests and directory notifications reach only clients holding the same one. `Sessions:RequireServerPassword` defaults to true and rejects clients that present none; disabling it puts passwordless clients into one open pool and the client warns about it.
- Changing the password takes effect on the relay immediately rather than at the next hub call, and a published host moves to the new group with the connection that published it. The password it left can no longer list or reach it, and a request that no longer shares its group is cancelled before it can be approved. An annotator already approved keeps its connection, which the host ends from that annotator's row or with **Disconnect all**.
- Only the derived key is stored on the client, under DPAPI `CurrentUser` alongside session credentials, never in the preferences file and never shown back to the user. Settings identifies the password in use by a short check code derived from that key under its own domain separator: clients showing the same code share a password, and recovering the password from a code still means guessing passwords through the same PBKDF2 cost.
- Directory entries expose the host's chosen display name, optional profile picture, and opaque session ID to the clients that share its password. Direct requests still require host approval before any annotator credential is issued.
- Annotator credentials are issued only after explicit approval from the session's host connection.
- Role and session membership are revalidated for every pointer, acknowledgement, resume, and termination operation.
- Reconnect requires the client-instance ID, session token, role, and a single-use rotating reconnect token.
- Pointer events are rejected for invalid coordinates, stale TTL, future timestamps, wrong sessions, unauthorized roles, excessive rate, and duplicate/old sequence numbers.
- The pointer rate limit is metered per approved annotator, so an abusive annotator exhausts only its own budget and cannot throttle the other annotators in the session.
- SignalR receive payloads are limited to 32 KB; each freehand batch is separately limited to 128 validated normalized points.
- Structured logs omit session secrets, role tokens, reconnect tokens, and individual pointer coordinates.
- Production refuses plaintext requests by default; the Docker profile permits it only on the unexposed relay container port behind Caddy. No certificate-validation bypass exists.

## Phase 5 client controls

- The client accepts only HTTPS relay URLs.
- TLS certificate validation uses the platform default; the production constructor has no message-handler or certificate-validation override.
- Only the random client-instance ID is persisted in plaintext under the current user's local application data.
- Calibration geometry lasts only for the process session and is never sent to the relay.
- Pointer sends are dropped while disconnected or reconnecting and are not replayed. Endpoint disconnect revokes annotator access, so annotating resumes only after a fresh request and approval.
- The host repeats structural and TTL validation immediately before display and acknowledges only displayed markers.
- Termination or failed resume removes the overlay, exits annotating, and clears the in-memory session state.

## Phase 6 hardening controls

- Production returns HTTP 400 for plaintext by default and never redirects secrets to another URL. Secure production responses use HSTS.
- The Docker profile explicitly allows HTTP only on the unexposed relay container port; Caddy is the sole published service and terminates HTTPS on port 443.
- SignalR detailed errors are disabled, unexpected operations are audited by a hub filter, and safe production exception handling avoids stack-trace disclosure.
- Client role and reconnect credentials are encrypted with Windows DPAPI `CurrentUser`, written atomically, cleared on normal shutdown, and never fall back to plaintext.
- Protected recovery state is rejected when corrupt, expired, wrong-role, or bound to a different durable client ID. An ungracefully interrupted host may recover only an empty session shell; successful recovery rotates and re-protects the reconnect token.
- Client crash handling preserves only protected credentials. Calibration rectangles, pointer events, and coordinates are never persisted.
- Server and client use stable structured audit events. Client audit schema deliberately has no arbitrary message or coordinate fields and records exception type/code rather than exception text.
- Release builds use latest installed .NET analyzers with warnings as errors. The Phase 6 NuGet advisory scan found no known vulnerable direct or transitive packages.

## Phase 7 deployment controls

- Inno Setup installs the self-contained x64 client per user and requests no administrator rights.
- The installer contains only Caddy's public CA root and adds it to the current user's root store when the clearly labelled HTTPS task is selected. Caddy's CA private key remains in its persistent server-side data volume.
- The relay URL is entered by the user on first launch, must use HTTPS, and still uses normal Windows certificate validation.
- Client audit and DPAPI session files remain per-user and are not silently deleted by uninstall.
- Endpoints require outbound TCP 443 only. The package creates no inbound firewall rule, Windows service, driver, remote-control capability, or certificate-validation bypass.
- The unsigned installer is distributed through an authenticated internal channel with a separately recorded SHA-256 digest. This provides provenance checking for the pilot but is not equivalent to Authenticode.

See [threat-model.md](threat-model.md) for trust boundaries, abuse cases, assumptions, and residual risks.
