# Threat model

## Scope and security objectives

This model covers the Windows client, the ASP.NET Core relay, Caddy, the shared contracts, local client state, and the HTTPS/WSS connection between them. Conferencing applications, organization identity infrastructure, and endpoint management are external dependencies.

The primary objectives are to prevent remote control, prevent unintended disclosure of screen or input content, restrict pointer delivery to explicitly approved presenters and one receiver, keep credentials confidential, reject stale or abusive traffic, and fail closed when identity or transport state is uncertain.

## Assets and trust boundaries

| Asset | Location | Protection |
| --- | --- | --- |
| Server password | Client memory during entry; derived key in a DPAPI-protected current-user file | Never sent to the relay or written to the preferences file; PBKDF2-SHA256 derivation makes a leaked key expensive to attack offline |
| Receiver name and profile picture | Client settings and relay session memory | Published only to clients presenting the same derived key; sent to the relay over TLS; never persisted server-side |
| Session and reconnect tokens | Client memory and DPAPI-protected current-user file | TLS in transit, DPAPI at rest, hashes on relay |
| Session secret | Receiver memory | Cryptographic generation; never logged or sent to presenter |
| Normalized pointer event | Client memory and relay transit | TLS, TTL, authorization, sequence window, rate limit; never persisted |
| Deliberate text annotation | Client memory and relay transit | Explicit editor, 256-character limit, plain-text rendering, TLS, TTL, authorization; never persisted or logged |
| Receiver display metadata | Session memory | Session membership and role authorization |
| Audit records | Server structured log and current-user client JSONL | No coordinates, tokens, screen data, or exception messages |

Trust boundaries exist at the public relay listener, SignalR hub method boundary, JSON deserialization boundary, Windows user-profile boundary, and every transition from normalized coordinates to a desktop overlay.

## Data flow

1. The receiver creates a session over HTTPS, receives role credentials, and opts into the relay directory.
2. The presenter selects the receiver's opaque directory entry and submits it with a durable random client ID. The receiver explicitly approves the displayed machine identity.
3. The relay issues role-specific credentials and relays only validated, transient normalized coordinates.
4. The receiver displays a non-interactive marker and acknowledges its event ID.
5. Receiver transport recovery submits the DPAPI-recovered token set; the relay revalidates it and rotates the reconnect token. All presenter memberships are revoked first and require fresh approval.

No flow contains pixels, window titles, processes, keystrokes, clipboard content, files, audio, or injected input.

## Threat analysis

| Threat | Control | Residual risk |
| --- | --- | --- |
| Session-identity guessing | 256-bit random session IDs, published only inside a password-scoped directory, explicit receiver approval before any credential | Online attempts are not globally throttled by source IP in the MVP; network perimeter controls remain required |
| Receiver-directory enumeration | Server password scopes every listing, join and notification to clients holding the same one; operator-controlled discovery, off in the shipped `.env.example`; receiver opt-in per session; approval before credentials | Directory entries carry the receiver's chosen name and profile picture, so everyone holding the password sees them without approval; the password is shared human-to-human, cannot be revoked for one person, and a relay with `RequireServerPassword=false` puts every passwordless client in one open pool |
| Presenter impersonation | Random client identity, receiver-visible machine name, explicit approval, role token | Machine name is not cryptographic identity until optional Entra ID is added |
| Credential theft from disk | Windows DPAPI CurrentUser encryption and no plaintext fallback | Malware running as the same user can call DPAPI and remains outside the app's isolation capability |
| Token replay | Session/role/client binding and single-use reconnect-token rotation | A token stolen from live process memory can be used until rotation or expiry |
| Stale or duplicate pointers | Two-second TTL, event ID, bounded sequence window, no reconnect queue | Clock error can reject legitimate events; managed endpoint time synchronization is assumed |
| Pointer flooding | 32 KB hub message ceiling, 128-point batch bound, one parallel invocation, and a per-sender token bucket of 90/s with burst 180 | An approved sender's budget is its own, so a session's total ceiling grows with the presenter limit the receiver chose; distributed connection exhaustion requires reverse-proxy/network controls |
| Unauthorized receiver control | Receiver display selection is local only; overlay never injects input | A local user can intentionally choose the wrong monitor |
| Overlay input interception | Receiver uses transparent/no-activate native styles; presenter capture is bounded to calibrated window and pointing state | WPF/Windows defects could affect focus; manual release testing remains required |
| TLS downgrade or invalid certificate | Caddy is the only published container port; client configuration requires HTTPS; platform validation trusts the per-user Caddy root and has no bypass | Replacing or losing the Caddy data volume requires rebuilding/reinstalling the client package with the new public root |
| Sensitive log disclosure | Stable structured audit fields omit secrets, coordinates, exception messages, and screen metadata | Session IDs and machine/client identifiers remain operational metadata |
| Server or client crash | Server fails closed on restart; endpoint disconnect revokes presenter memberships, and an ungracefully interrupted receiver can recover only an empty session shell | Relay restart requires every client to publish and request access again by design in the in-memory MVP |
| Multiple clients under one Windows profile | Role files and durable identity are scoped to the Windows user; automatic recovery is skipped when both saved roles exist | Concurrent same-profile processes support new local sessions, but automatic empty receiver-shell recovery requires one saved role per profile; production assumes distinct users/endpoints |
| Malformed input or implementation fault | Strict JSON, contract validation, hidden SignalR details, production exception handler, WPF crash boundary | Unknown platform defects and denial of service remain possible |

## Abuse cases verified by tests

- Unapproved and third-party connections cannot send pointers.
- Rejected joins receive no session data.
- Invalid, stale, future, duplicate, significantly old, oversized, and over-rate events fail or are ignored.
- The thirty-first immediate pointer exceeds the configured burst, and one sender exhausting its budget does not consume another sender's.
- Production plaintext requests receive an error and no redirect.
- Corrupt, expired, wrong-user, or wrong-role protected state is discarded.
- Receiver recovery rotates the reconnect token, but pointer delivery cannot resume until the presenter submits a new request and is approved again.

## Operational assumptions

- Windows 11 endpoints are managed, patched, time synchronized, and protected from same-user malware. Each production participant uses a distinct Windows user profile/endpoint.
- The Caddy public root is distributed only inside the intended installer; its private key remains in the persistent Docker volume.
- Firewall policy exposes only Caddy TCP 443. Relay port 8080 remains private to the Compose network.
- Audit sinks, access controls, retention, and alerting are configured by operations.

Review this model when adding Entra ID, durable server sessions, additional presenter/receiver roles, new pointer kinds, installer privileges, telemetry, or any new captured data.
