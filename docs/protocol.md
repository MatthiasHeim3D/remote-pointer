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
- `JoinRequest`: one-time pairing code, requested role, durable client-instance identity, and version.
- `DirectJoinRequest`: opaque session identity for an explicitly visible receiver, durable client-instance identity, and version.
- `ClientProfile`: optional PNG profile thumbnail capped to fit within the relay message limit.
- `AvailableReceiverDescriptor`: opaque session identity, receiver-selected label, process-scoped application identity, and an optional bounded PNG profile thumbnail; no pairing code, display metadata, or credential.
- `RelayCapabilities`: server-controlled receiver-discovery availability.
- `SessionStateMessage`: session identity, approval state, current receiver display, expiry, discovery state, and receiver-visible connected-presenter names.
- `SessionCredential`: role-restricted session token, rotating reconnect token, durable client identity, and expiry.
- `CreateSessionResponse`: pairing information, receiver-only session secret, and receiver credential.
- `JoinResponse`: acceptance result with no session data on rejection.
- `PresenterDescriptor`: pending presenter identity shown to the receiver for approval.
- `SessionResumeRequest`: session, role, client identity, and both required tokens.

The default pointer TTL is 2,000 ms at the client. Structural validation currently permits a configurable maximum of 10,000 ms and a configurable future clock skew of 5,000 ms. Server configuration will constrain production values.

## Validation layers

1. JSON parsing rejects malformed or unexpected fields.
2. Contract validation rejects missing identities, invalid enums, invalid display metadata, non-finite/out-of-range coordinates, invalid TTLs, expired events, and implausible future timestamps.
3. The relay validates live session state, the approved presenter identity, role permissions, replay/reordering, message size, and rate limits.

Pairing codes normalize case, whitespace, and hyphens. The accepted six-character alphabet excludes `0`, `O`, `1`, and `I` to reduce transcription errors. Codes are generated cryptographically, stored only by hash, expire after ten minutes when unused, and are consumed by the first accepted join request.

When `Sessions:ReceiverDiscoveryEnabled` is true, an active receiver can explicitly publish its machine label and opaque session ID. A presenter may submit a direct join request for that entry, but receives no credential and cannot send pointers until the receiver approves. Disabled, hidden, disconnected, pending, and full-capacity receivers are omitted. A receiver chooses a maximum presenter count when creating the session; the client default is two and the server maximum is sixteen. A discoverable receiver can remain available after its pairing code expires, until the receiver reaches capacity, hides/ends it, or the session expires.

## SignalR surface

Clients connect to `/hubs/pointer` with a persistent `clientInstanceId`, a process-scoped `applicationInstanceId`, and an optional approval `displayName`. The process-scoped identity prevents a running client from discovering or joining its own receiver session while still allowing separate client processes on the same machine. Implemented client-to-server methods are:

- `CreateReceiverSession(DisplayDescriptor)`
- `CreateReceiverSessionWithProfile(DisplayDescriptor, ClientProfile)`
- `CreateReceiverSessionWithSettings(DisplayDescriptor, ClientProfile, maximumPresenterConnections)`
- `GetRelayCapabilities()`
- `GetAvailableReceivers()`
- `SetReceiverDiscoverable(sessionId, discoverable)`
- `RequestToJoinSession(JoinRequest)`
- `RequestToJoinReceiver(DirectJoinRequest)`
- `ApprovePresenter(sessionId, presenterConnectionId)`
- `UpdateReceiverDisplay(sessionId, DisplayDescriptor)`
- `SendPointer(PointerEventMessage)`
- `AcknowledgePointer(PointerAcknowledgement)`
- `ResumeSession(SessionResumeRequest)`
- `EndSession(sessionId)`

Implemented server-to-client methods are `PresenterJoinRequested`, `SessionCredentialIssued`, `SessionApproved`, `ReceiverDisplayChanged`, `PointerReceived`, `PointerDisplayed`, and `SessionEnded`.

The desktop client uses automatic reconnect delays from `appsettings.json`. An active role resumes with `SessionResumeRequest`; the returned credential contains the rotated reconnect token. A failed resume clears local session state. Pending, unapproved presenter requests have no credential and therefore must be submitted again after a connection loss.

For client-process recovery, approved role credentials are stored as versioned DPAPI-protected documents. Startup sends the same `SessionResumeRequest` used by automatic reconnect. Successful resume replaces the stored reconnect token with the server's rotated value before subsequent recovery. The recovery file contains no pointer event, coordinate, calibration rectangle, pairing code, or display image.
