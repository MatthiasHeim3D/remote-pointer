# Security

## Narrow data boundary

The protocol contains only display geometry, normalized pointer events, acknowledgements, and session metadata. The solution has no screen-capture, audio, input-injection, clipboard, file-transfer, process-inspection, UI Automation, or conferencing-integration API.

The shared library does not reference Windows desktop APIs. Future Win32 interop is restricted to monitor geometry, overlay styles, display-change messages, and global hotkey registration under `RemotePointer.Client/Native`.

## Phase 1 controls

- Strict JSON parsing rejects unknown fields and integer enum coercion.
- All normalized coordinates must be finite and within the inclusive unit interval.
- Pointer timestamps and TTLs are validated before an event can be considered for display.
- Event IDs and a bounded sequence-number window support replay rejection.
- Friendly pairing codes exclude visually ambiguous characters.
- Nullable analysis, .NET analyzers, and warnings-as-errors are enabled repository-wide.

## Controls scheduled for later phases

- HTTPS/WSS enforcement and production plaintext refusal.
- SignalR connection authentication and role-restricted session tokens.
- Cryptographically generated session IDs, secrets, and hashed one-time pairing codes.
- Explicit receiver approval and one-presenter enforcement.
- Per-session expiry, termination, rate limiting, and 8 KB message limits.
- Coordinate-free structured audit logging and secret redaction.
- Optional tenant/group-restricted Microsoft Entra ID authentication.

No development certificate-bypass switch will be included in production builds.
