# Protocol

## Encoding

Messages use UTF-8 JSON. The shared serializer policy uses camel-case property names and lower camel-case string enum values. Integer enum values, comments, trailing commas, non-standard numeric values, case-mismatched property names, and unknown members are rejected.

The relay configures a maximum SignalR receive message size of 32 KB and allows only one parallel hub invocation per client.

## Coordinate system

The annotator maps a point inside its calibrated target rectangle as follows:

```text
x = clamp((clickX - rectangleLeft) / rectangleWidth, 0, 1)
y = clamp((clickY - rectangleTop) / rectangleHeight, 0, 1)
```

The host maps the normalized point into the selected overlay rectangle:

```text
x = overlayLeft + normalizedX * overlayWidth
y = overlayTop  + normalizedY * overlayHeight
```

Normalized coordinates are finite numbers in the inclusive range `0.0` through `1.0`. The rectangle origin may be negative. Rectangle dimensions must be finite and greater than zero.

The annotator sends each captured pointer event immediately as a `PointerEventMessage`. A click is one event. Paths, lines, rectangles, and circles use start/update/end events sharing a gesture ID. A circle starts at its center and its radius is the distance from that center to the current pointer position. Updates are delivered at roughly 60 Hz, with freehand updates carrying bounded batches of every captured mouse sample so spatial detail is retained. A 500 ms keepalive keeps stationary held gestures active; only the end event begins the normal fade. Text annotations carry at most 256 plain-text characters and are finalized explicitly with Enter. The host revalidates every event immediately before display and returns `PointerAcknowledgement` only after the overlay accepts it. How it then draws an accepted event is a presentation choice rather than part of the protocol: it paces arriving points onto its render loop and lightly smooths freehand ones, and always assigns a released gesture its final coordinate exactly. No pointer event or text is persisted or queued during a connection interruption.

## Initial messages

- `DisplayDescriptor`: stable display identity, friendly name, pixel dimensions, scale, and clockwise rotation.
- `PointerEventMessage`: unique event ID, session identity, monotonic sequence, normalized coordinate, kind, send time, and TTL.
- `PointerAcknowledgement`: event identity and host display time.
- `DirectJoinRequest`: opaque session identity for an explicitly visible host, durable client-instance identity, and version.
- `ClientProfile`: optional PNG profile thumbnail capped to fit within the relay message limit.
- `AvailableHostDescriptor`: opaque session identity, host-selected label, process-scoped application identity, and an optional bounded PNG profile thumbnail; no display metadata or credential.
- `RelayCapabilities`: whether the relay requires a server password.
- `SessionStateMessage`: session identity, approval state, current host display, expiry, discovery state, and host-visible connected-annotator names.
- `SessionCredential`: role-restricted session token, rotating reconnect token, durable client identity, and expiry.
- `CreateSessionResponse`: opaque session identity, host-only session secret, and host credential.
- `JoinResponse`: acceptance result with no session data on rejection.
- `AnnotatorDescriptor`: pending annotator identity shown to the host for approval.
- `SessionResumeRequest`: session, role, client identity, and both required tokens.

The default pointer TTL is 2,000 ms at the client. Structural validation currently permits a configurable maximum of 10,000 ms and a configurable future clock skew of 5,000 ms. Server configuration will constrain production values.

## Validation layers

1. JSON parsing rejects malformed or unexpected fields.
2. Contract validation rejects missing identities, invalid enums, invalid display metadata, non-finite/out-of-range coordinates, invalid TTLs, expired events, and implausible future timestamps.
3. The relay validates live session state, the approved annotator identity, role permissions, replay/reordering, message size, and rate limits. The pointer rate limit is metered per approved annotator — 90 events per second with a burst of 180 by default — which is above the client's own update rate, so reaching it means a faulty or abusive annotator and the event is rejected rather than dropped silently.

`EnterRelayGroup` carries the key the client derives from its server password with PBKDF2-SHA256 — never the password, and passed as a hub argument rather than a connection query parameter so it stays out of proxy access logs. The relay holds it per connection, so it is presented again after every connect and reconnect, and two clients share a group only by deriving the same value. Listings, join requests and directory notifications never cross groups. When `Sessions:RequireServerPassword` is true a client that presents no key can neither publish itself nor list or reach anyone; when it is false such clients share one open pool.

An active host publishes its chosen display name, optional profile picture, and opaque session ID to the clients that share its server password. A new session starts published and the host can hide it at any time. An annotator may submit a direct join request for a listed entry, but receives no credential and cannot send pointers until the host approves. Hidden, disconnected, pending, and full-capacity hosts are omitted from the listing. A host chooses a maximum annotator count when creating the session; the client default is two and the server maximum is sixteen. A published host stays available until it reaches capacity, hides/ends the session, or the session expires. The directory is the only way into a session; there is no other join path.

A session that nobody has asked to join is collected once `Sessions:AbandonedSessionLifetimeMinutes` — ten minutes by default — has passed, unless something can still reach it. A connected host that chose to be invisible can publish itself again and keeps its session; a hidden, disconnected shell cannot be joined and is collected.

## HTTP surface

Besides the hub, the relay serves two unauthenticated GET endpoints:

- `/health`: the health-check result.
- `/version`: `ServerVersionResponse` — the constant product id `remote-pointer-relay` and the relay's build version without the commit metadata that Nerdbank.GitVersioning appends.

The settings-pane connection test uses both. `/health` establishes reachability, then `/version` establishes identity: the response must be `application/json`, must be small, and must carry the expected product id. Reachability alone is not enough, because `/health` is a common path that any unrelated host may answer. A host that fails the identity check is reported as "not a Remote Pointer server" and its address is not saved. The advertised version is shown next to the verified checkmark under the server address field.

## SignalR surface

Clients connect to `/hubs/pointer` with a persistent `clientInstanceId`, a process-scoped `applicationInstanceId`, and an optional approval `displayName`. The process-scoped identity prevents a running client from discovering or joining its own host session while still allowing separate client processes on the same machine. Implemented client-to-server methods are:

- `GetRelayCapabilities()`
- `EnterRelayGroup(groupKey)`
- `GetAvailableHosts()`
- `CreateHostSession(DisplayDescriptor, ClientProfile, maximumAnnotatorConnections, displayName)`
- `SetHostDiscoverable(sessionId, discoverable)`
- `RequestToJoinHost(DirectJoinRequest, displayName)`
- `UpdateHostDisplay(sessionId, DisplayDescriptor)`
- `UpdateHostClientSettings(sessionId, displayName, ClientProfile, maximumAnnotatorConnections)`
- `ApproveAnnotator(sessionId, annotatorConnectionId)`
- `RejectAnnotator(sessionId, annotatorConnectionId)`
- `SendPointer(PointerEventMessage)`
- `AcknowledgePointer(PointerAcknowledgement)`
- `ResumeSession(SessionResumeRequest)`
- `EndSession(sessionId)`
- `DisconnectAllConnections(sessionId)`
- `DisconnectAnnotator(sessionId, annotatorId)`
- `SetAnnotatorPaused(sessionId, annotatorId, paused)`

A blank `displayName` falls back to the one supplied as a connection parameter. A
host that requests more annotator connections than the relay allows is rejected
rather than reduced, so the limit it asks for is the limit it gets.

The `annotatorId` both host methods take is the annotator's `clientInstanceId`, reported to the host in `ConnectedAnnotatorDescriptor`. It is used rather than the relay connection id because it survives the annotator reconnecting. `SetAnnotatorPaused` with a null `annotatorId` applies to every connected annotator.

Implemented server-to-client methods are `AnnotatorJoinRequested`, `AnnotatorJoinCancelled`, `SessionCredentialIssued`, `SessionApproved`, `HostDisplayChanged`, `PointerReceived`, `PointerDisplayed`, `AnnotationPaused`, `SessionEnded`, and `HostDirectoryChanged`. `HostDirectoryChanged` is broadcast whenever the directory could have changed — including when the relay collects an expired session, which no client asked for — and carries no payload: a client that cares re-reads the directory with `GetAvailableHosts`. It reaches the group the affected session was published in, which is not always the group of the connection that caused the change, because an approved annotator that changes its server password keeps its place in the session it was admitted to. A client that cannot act on a notification when it arrives, because a session of its own owns the listing, re-reads the directory when that session ends rather than dropping the notification.

A pause is not a disconnect: the annotator keeps its session, its credential, and its place in the host's list, and only its pointer events stop being relayed. The relay drops them silently rather than rejecting them, so an event already in flight when the pause took effect does not surface as an error, and the paused annotator is told through `AnnotationPaused` so it can show that its input is going nowhere. The pause is stored on the annotator record, so it survives a reconnect and is repeated to the resuming annotator in the `SessionApproved` state.

The relay stamps `PointerEventMessage.AnnotatorId` with the sending annotator on the way to the host; whatever an annotator puts there is replaced, so the host can attribute a drawing to a listed annotator without trusting the sender.

`EndSession` means different things by role. The host ends the whole session; an approved annotator leaves it; an annotator still waiting for approval withdraws its request, which the relay reports to the host as `AnnotatorJoinCancelled` so the approval prompt closes.

The desktop client uses automatic reconnect delays from `appsettings.json`. An active host may resume its offline session shell with `SessionResumeRequest`; the returned credential contains the rotated reconnect token. Disconnecting the host immediately revokes every approved and pending annotator, and disconnecting an annotator revokes that annotator credential, so annotator access always requires a new request after either endpoint connection is lost. A failed resume clears local session state.

Role credentials are stored as versioned DPAPI-protected documents for interrupted-transport recovery. A normal process shutdown explicitly ends its session and deletes the document. After an ungraceful host exit, startup may resume only the host's empty session shell; every former annotator has already been revoked and must request approval again. Successful resume replaces the stored reconnect token. The recovery file contains no pointer event, coordinate, calibration rectangle, or display image.
