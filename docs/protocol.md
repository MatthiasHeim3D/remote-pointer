# Protocol

## Encoding

Messages use UTF-8 JSON. The shared serializer policy uses camel-case property names and lower camel-case string enum values. Integer enum values, comments, trailing commas, non-standard numeric values, case-mismatched property names, and unknown members are rejected.

The relay configures a maximum SignalR receive message size of 32 KB and allows only one parallel hub invocation per client.

## Coordinate system

The presenter maps a point inside its calibrated target rectangle as follows:

```text
x = clamp((clickX - rectangleLeft) / rectangleWidth, 0, 1)
y = clamp((clickY - rectangleTop) / rectangleHeight, 0, 1)
```

The receiver maps the normalized point into the selected overlay rectangle:

```text
x = overlayLeft + normalizedX * overlayWidth
y = overlayTop  + normalizedY * overlayHeight
```

Normalized coordinates are finite numbers in the inclusive range `0.0` through `1.0`. The rectangle origin may be negative. Rectangle dimensions must be finite and greater than zero.

The presenter sends each captured pointer event immediately as a `PointerEventMessage`. A click is one event. Paths, lines, rectangles, and circles use start/update/end events sharing a gesture ID. A circle starts at its center and its radius is the distance from that center to the current pointer position. Updates are delivered at roughly 60 Hz, with freehand updates carrying bounded batches of every captured mouse sample so spatial detail is retained. A 500 ms keepalive keeps stationary held gestures active; only the end event begins the normal fade. Text annotations carry at most 256 plain-text characters and are finalized explicitly with Enter. The receiver revalidates every event immediately before display and returns `PointerAcknowledgement` only after the overlay accepts it. No pointer event or text is persisted or queued during a connection interruption.

## Initial messages

- `DisplayDescriptor`: stable display identity, friendly name, pixel dimensions, scale, and clockwise rotation.
- `PointerEventMessage`: unique event ID, session identity, monotonic sequence, normalized coordinate, kind, send time, and TTL.
- `PointerAcknowledgement`: event identity and receiver display time.
- `DirectJoinRequest`: opaque session identity for an explicitly visible receiver, durable client-instance identity, and version.
- `ClientProfile`: optional PNG profile thumbnail capped to fit within the relay message limit.
- `AvailableReceiverDescriptor`: opaque session identity, receiver-selected label, process-scoped application identity, and an optional bounded PNG profile thumbnail; no display metadata or credential.
- `RelayCapabilities`: server-controlled receiver-discovery availability and whether a server password is required.
- `SessionStateMessage`: session identity, approval state, current receiver display, expiry, discovery state, and receiver-visible connected-presenter names.
- `SessionCredential`: role-restricted session token, rotating reconnect token, durable client identity, and expiry.
- `CreateSessionResponse`: opaque session identity, receiver-only session secret, and receiver credential.
- `JoinResponse`: acceptance result with no session data on rejection.
- `PresenterDescriptor`: pending presenter identity shown to the receiver for approval.
- `SessionResumeRequest`: session, role, client identity, and both required tokens.

The default pointer TTL is 2,000 ms at the client. Structural validation currently permits a configurable maximum of 10,000 ms and a configurable future clock skew of 5,000 ms. Server configuration will constrain production values.

## Validation layers

1. JSON parsing rejects malformed or unexpected fields.
2. Contract validation rejects missing identities, invalid enums, invalid display metadata, non-finite/out-of-range coordinates, invalid TTLs, expired events, and implausible future timestamps.
3. The relay validates live session state, the approved presenter identity, role permissions, replay/reordering, message size, and rate limits. The pointer rate limit is metered per approved presenter — 90 events per second with a burst of 180 by default — which is above the client's own update rate, so reaching it means a faulty or abusive sender and the event is rejected rather than dropped silently.

`EnterRelayGroup` carries the key the client derives from its server password with PBKDF2-SHA256 — never the password, and passed as a hub argument rather than a connection query parameter so it stays out of proxy access logs. The relay holds it per connection, so it is presented again after every connect and reconnect, and two clients share a group only by deriving the same value. Listings, join requests and directory notifications never cross groups. When `Sessions:RequireServerPassword` is true a client that presents no key can neither publish itself nor list or reach anyone; when it is false such clients share one open pool.

When `Sessions:ReceiverDiscoveryEnabled` is true, an active receiver can explicitly publish its chosen display name, optional profile picture, and opaque session ID to the clients that share its server password. A presenter may submit a direct join request for that entry, but receives no credential and cannot send pointers until the receiver approves. Disabled, hidden, disconnected, pending, and full-capacity receivers are omitted. A receiver chooses a maximum presenter count when creating the session; the client default is two and the server maximum is sixteen. A discoverable receiver stays available until it reaches capacity, hides/ends the session, or the session expires. This is the only way into a session: a relay with discovery switched off accepts no join request.

A session that nobody has asked to join is collected once `Sessions:AbandonedSessionLifetimeMinutes` — ten minutes by default — has passed, unless something can still reach it. A connected receiver that chose to be invisible can publish itself again and keeps its session; a disconnected shell, or any session on a relay with discovery switched off, cannot be joined and is collected.

## SignalR surface

Clients connect to `/hubs/pointer` with a persistent `clientInstanceId`, a process-scoped `applicationInstanceId`, and an optional approval `displayName`. The process-scoped identity prevents a running client from discovering or joining its own receiver session while still allowing separate client processes on the same machine. Implemented client-to-server methods are:

- `GetRelayCapabilities()`
- `EnterRelayGroup(groupKey)`
- `GetAvailableReceivers()`
- `CreateReceiverSession(DisplayDescriptor, ClientProfile, maximumPresenterConnections, displayName)`
- `SetReceiverDiscoverable(sessionId, discoverable)`
- `RequestToJoinReceiver(DirectJoinRequest, displayName)`
- `UpdateReceiverDisplay(sessionId, DisplayDescriptor)`
- `UpdateReceiverClientSettings(sessionId, displayName, ClientProfile, maximumPresenterConnections)`
- `ApprovePresenter(sessionId, presenterConnectionId)`
- `RejectPresenter(sessionId, presenterConnectionId)`
- `SendPointer(PointerEventMessage)`
- `AcknowledgePointer(PointerAcknowledgement)`
- `ResumeSession(SessionResumeRequest)`
- `EndSession(sessionId)`
- `DisconnectAllConnections(sessionId)`

A blank `displayName` falls back to the one supplied as a connection parameter. A
receiver that requests more presenter connections than the relay allows is rejected
rather than reduced, so the limit it asks for is the limit it gets.

Implemented server-to-client methods are `PresenterJoinRequested`, `PresenterJoinCancelled`, `SessionCredentialIssued`, `SessionApproved`, `ReceiverDisplayChanged`, `PointerReceived`, `PointerDisplayed`, `SessionEnded`, and `ReceiverDirectoryChanged`. `ReceiverDirectoryChanged` is broadcast to every connected client whenever the directory could have changed, and carries no payload: a client that cares re-reads the directory with `GetAvailableReceivers`.

`EndSession` means different things by role. The receiver ends the whole session; an approved presenter leaves it; a presenter still waiting for approval withdraws its request, which the relay reports to the receiver as `PresenterJoinCancelled` so the approval prompt closes.

The desktop client uses automatic reconnect delays from `appsettings.json`. An active receiver may resume its offline session shell with `SessionResumeRequest`; the returned credential contains the rotated reconnect token. Disconnecting the receiver immediately revokes every approved and pending presenter, and disconnecting a presenter revokes that presenter credential, so presenter access always requires a new request after either endpoint connection is lost. A failed resume clears local session state.

Role credentials are stored as versioned DPAPI-protected documents for interrupted-transport recovery. A normal process shutdown explicitly ends its session and deletes the document. After an ungraceful receiver exit, startup may resume only the receiver's empty session shell; every former presenter has already been revoked and must request approval again. Successful resume replaces the stored reconnect token. The recovery file contains no pointer event, coordinate, calibration rectangle, or display image.
