# Threat model

## Scope and security objectives

This model covers the Windows client, the ASP.NET Core relay, the shared contracts, local client state, and the HTTPS/WSS connection between them. Conferencing applications, organization identity infrastructure, endpoint management, and PKI operations are external dependencies.

The primary objectives are to prevent remote control, prevent disclosure of screen or input content, restrict pointer delivery to one explicitly approved presenter and receiver, keep credentials confidential, reject stale or abusive traffic, and fail closed when identity or transport state is uncertain.

## Assets and trust boundaries

| Asset | Location | Protection |
| --- | --- | --- |
| Session and reconnect tokens | Client memory and DPAPI-protected current-user file | TLS in transit, DPAPI at rest, hashes on relay |
| Pairing code | Receiver UI and transient relay response | One-time, ten-minute lifetime, hash-only relay storage |
| Session secret | Receiver memory | Cryptographic generation; never logged or sent to presenter |
| Normalized pointer event | Client memory and relay transit | TLS, TTL, authorization, sequence window, rate limit; never persisted |
| Receiver display metadata | Session memory | Session membership and role authorization |
| Audit records | Server structured log and current-user client JSONL | No coordinates, tokens, pairing codes, screen data, or exception messages |

Trust boundaries exist at the public relay listener, SignalR hub method boundary, JSON deserialization boundary, Windows user-profile boundary, and every transition from normalized coordinates to a desktop overlay.

## Data flow

1. The receiver creates a session over HTTPS and receives role credentials plus a one-time code.
2. The presenter submits the code and a durable random client ID. The receiver explicitly approves the displayed machine identity.
3. The relay issues role-specific credentials and relays only validated, transient normalized coordinates.
4. The receiver displays a non-interactive marker and acknowledges its event ID.
5. Reconnection or client restart submits the DPAPI-recovered token set; the relay revalidates it and rotates the reconnect token.

No flow contains pixels, window titles, processes, keystrokes, clipboard content, files, audio, or injected input.

## Threat analysis

| Threat | Control | Residual risk |
| --- | --- | --- |
| Pairing-code guessing | Cryptographic alphabet, short expiry, one-time consumption, explicit receiver approval | Online attempts are not globally throttled by source IP in the MVP; network perimeter controls remain required |
| Presenter impersonation | Random client identity, receiver-visible machine name, explicit approval, role token | Machine name is not cryptographic identity until optional Entra ID is added |
| Credential theft from disk | Windows DPAPI CurrentUser encryption and no plaintext fallback | Malware running as the same user can call DPAPI and remains outside the app's isolation capability |
| Token replay | Session/role/client binding and single-use reconnect-token rotation | A token stolen from live process memory can be used until rotation or expiry |
| Stale or duplicate pointers | Two-second TTL, event ID, bounded sequence window, no reconnect queue | Clock error can reject legitimate events; managed endpoint time synchronization is assumed |
| Pointer flooding | 8 KB hub message ceiling, one parallel invocation, 20/s token bucket and burst 30 | Distributed connection exhaustion requires reverse-proxy/network controls |
| Unauthorized receiver control | Receiver display selection is local only; overlay never injects input | A local user can intentionally choose the wrong monitor |
| Overlay input interception | Receiver uses transparent/no-activate native styles; presenter capture is bounded to calibrated window and pointing state | WPF/Windows defects could affect focus; manual release testing remains required |
| TLS downgrade or invalid certificate | Production server rejects HTTP; client configuration requires HTTPS; platform certificate validation has no production handler override | Organization PKI issuance and renewal are operational dependencies |
| Sensitive log disclosure | Stable structured audit fields omit secrets, coordinates, exception messages, and screen metadata | Session IDs and machine/client identifiers remain operational metadata |
| Server or client crash | Server fails closed and loses in-memory sessions; client credentials are protected and can resume only while relay session survives | Relay restart requires re-pairing by design in the in-memory MVP |
| Multiple clients under one Windows profile | Role files and durable identity are scoped to the Windows user; automatic recovery is skipped when both saved roles exist | Concurrent same-profile processes support new local sessions, but automatic crash recovery requires one saved role per profile; production assumes distinct users/endpoints |
| Malformed input or implementation fault | Strict JSON, contract validation, hidden SignalR details, production exception handler, WPF crash boundary | Unknown platform defects and denial of service remain possible |

## Abuse cases verified by tests

- Unapproved and third-party connections cannot send pointers.
- Rejected joins receive no session data.
- Invalid, stale, future, duplicate, significantly old, oversized, and over-rate events fail or are ignored.
- The thirty-first immediate pointer exceeds the configured burst.
- Production plaintext requests receive an error and no redirect.
- Corrupt, expired, wrong-user, or wrong-role protected state is discarded.
- Crash recovery rotates the reconnect token before pointer delivery resumes.

## Operational assumptions

- Windows 11 endpoints are managed, patched, time synchronized, and protected from same-user malware. Each production participant uses a distinct Windows user profile/endpoint.
- The relay certificate chains to a trusted organization root and its private key is stored outside this repository.
- Firewall and reverse-proxy policy restrict relay exposure and connection-level denial of service.
- Audit sinks, access controls, retention, and alerting are configured by operations.

Review this model when adding Entra ID, durable server sessions, additional presenter/receiver roles, new pointer kinds, installer privileges, telemetry, or any new captured data.
