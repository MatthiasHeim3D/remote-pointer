# Threat model

## Scope and security objectives

This model covers the Windows client, the ASP.NET Core relay, Caddy, the shared contracts, local client state, and the HTTPS/WSS connection between them. Conferencing applications, organization identity infrastructure, and endpoint management are external dependencies.

The primary objectives are to prevent remote control, prevent unintended disclosure of screen or input content, restrict pointer delivery to explicitly approved annotators and one host, keep credentials confidential, reject stale or abusive traffic, and fail closed when identity or transport state is uncertain.

## Assets and trust boundaries

| Asset | Location | Protection |
| --- | --- | --- |
| Server password | Client memory during entry; derived key in a DPAPI-protected current-user file; relay configuration | Only the derived key crosses the network, in an `Authorization` header rather than the query string; never written to the client preferences file and never shown back; PBKDF2-SHA256 derivation makes a leaked key expensive to attack offline; the relay holds the derived key in memory and compares in constant time |
| Room name | Client preferences file, relay connection state, and the wire | None, deliberately: it is a label, not a secret, so that the user can read back which room they are in |
| Host name and profile picture | Client settings and relay session memory | Published only to clients admitted by the server password, and only to those in the same room; sent to the relay over TLS; never persisted server-side |
| Session and reconnect tokens | Client memory and DPAPI-protected current-user file | TLS in transit, DPAPI at rest, hashes on relay |
| Session secret | Host memory | Cryptographic generation; never logged or sent to annotator |
| Normalized pointer event | Client memory and relay transit | TLS, TTL, authorization, sequence window, rate limit; never persisted |
| Deliberate text annotation | Client memory and relay transit | Explicit editor, 256-character limit, plain-text rendering, TLS, TTL, authorization; never persisted or logged |
| Host display metadata | Session memory | Session membership and role authorization |
| Audit records | Server structured log and current-user client JSONL | No coordinates, tokens, screen data, or exception messages |

Trust boundaries exist at the public relay listener, SignalR hub method boundary, JSON deserialization boundary, Windows user-profile boundary, and every transition from normalized coordinates to a desktop overlay.

## Data flow

1. The host creates a session over HTTPS, receives role credentials, and opts into the relay directory.
2. The annotator selects the host's opaque directory entry and submits it with a durable random client ID. The host explicitly approves the displayed machine identity.
3. The relay issues role-specific credentials and relays only validated, transient normalized coordinates.
4. The host displays a non-interactive marker and acknowledges its event ID.
5. Host transport recovery submits the DPAPI-recovered token set; the relay revalidates it and rotates the reconnect token. All annotator memberships are revoked first and require fresh approval.

No flow contains pixels, window titles, processes, keystrokes, clipboard content, files, audio, or injected input.

## Threat analysis

| Threat | Control | Residual risk |
| --- | --- | --- |
| Session-identity guessing | 256-bit random session IDs, published only to clients the server password admitted, explicit host approval before any credential | Online attempts are not globally throttled by source IP in the MVP; network perimeter controls remain required |
| Relay access | The server password is required to open a connection; a client without it is refused at the handshake and never reaches the hub | The password is shared human-to-human and cannot be revoked for one person without changing it for everyone and restarting the relay; a relay started with no password configured admits anyone who can reach its address |
| Host-directory enumeration | Rooms scope every listing, join and notification to the clients in the same one; per-session host visibility; approval before credentials | Rooms are not an access control: any client the password admitted can name any room and list what is published there. The directory cannot be switched off, because it is the only join path, and entries carry the host's chosen name and profile picture, so everyone in the room sees them without approval |
| Annotator impersonation | Random client identity, host-visible machine name, explicit approval, role token | Machine name is not cryptographic identity until optional Entra ID is added |
| Credential theft from disk | Windows DPAPI CurrentUser encryption and no plaintext fallback | Malware running as the same user can call DPAPI and remains outside the app's isolation capability |
| Token replay | Session/role/client binding and single-use reconnect-token rotation | A token stolen from live process memory can be used until rotation or expiry |
| Stale or duplicate pointers | Two-second TTL, event ID, bounded sequence window, no reconnect queue | Clock error can reject legitimate events; managed endpoint time synchronization is assumed |
| Pointer flooding | 32 KB hub message ceiling, 128-point batch bound, one parallel invocation, and a per-annotator token bucket of 90/s with burst 180 | An approved annotator's budget is its own, so a session's total ceiling grows with the annotator limit the host chose; distributed connection exhaustion requires reverse-proxy/network controls |
| Unauthorized host control | Host display selection is local only; overlay never injects input | A local user can intentionally choose the wrong monitor |
| Overlay input interception | Host uses transparent/no-activate native styles; annotator capture is bounded to calibrated window and annotating state | WPF/Windows defects could affect focus; manual release testing remains required |
| TLS downgrade or invalid certificate | Caddy is the only published container port; client configuration requires HTTPS; platform validation trusts the per-user Caddy root and has no bypass | Replacing or losing the Caddy data volume requires rebuilding/reinstalling the client package with the new public root |
| Sensitive log disclosure | Stable structured audit fields omit secrets, coordinates, exception messages, and screen metadata | Session IDs and machine/client identifiers remain operational metadata |
| Server or client crash | Server fails closed on restart; endpoint disconnect revokes annotator memberships, and an ungracefully interrupted host can recover only an empty session shell | Relay restart requires every client to publish and request access again by design in the in-memory MVP |
| Multiple clients under one Windows profile | Role files and durable identity are scoped to the Windows user; automatic recovery is skipped when both saved roles exist | Concurrent same-profile processes support new local sessions, but automatic empty host-shell recovery requires one saved role per profile; production assumes distinct users/endpoints |
| Malformed input or implementation fault | Strict JSON, contract validation, hidden SignalR details, production exception handler, WPF crash boundary | Unknown platform defects and denial of service remain possible |

## Abuse cases verified by tests

- Unapproved and third-party connections cannot send pointers.
- Rejected joins receive no session data.
- Invalid, stale, future, duplicate, significantly old, oversized, and over-rate events fail or are ignored.
- The thirty-first immediate pointer exceeds the configured burst, and one annotator exhausting its budget does not consume another annotator's.
- Production plaintext requests receive an error and no redirect.
- Corrupt, expired, wrong-user, or wrong-role protected state is discarded.
- Host recovery rotates the reconnect token, but pointer delivery cannot resume until the annotator submits a new request and is approved again.

## Operational assumptions

- Windows 11 endpoints are managed, patched, time synchronized, and protected from same-user malware. Each production participant uses a distinct Windows user profile/endpoint.
- The Caddy public root is distributed only inside the intended installer; its private key remains in the persistent Docker volume.
- Firewall policy exposes only Caddy TCP 443. Relay port 8080 remains private to the Compose network.
- Audit sinks, access controls, retention, and alerting are configured by operations.

Review this model when adding Entra ID, durable server sessions, additional annotator/host roles, new pointer kinds, installer privileges, telemetry, or any new captured data.
