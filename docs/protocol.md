# Protocol

## Encoding

Messages use UTF-8 JSON. The shared serializer policy uses camel-case property names and lower camel-case string enum values. Integer enum values, comments, trailing commas, non-standard numeric values, case-mismatched property names, and unknown members are rejected.

The maximum 8 KB SignalR message size is a server transport concern and will be configured in Phase 4.

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

In Phase 3, presenter clicks are normalized locally and shown in the client status panel. They are not transmitted because relay networking is intentionally deferred. The same normalized event boundary will feed `PointerEventMessage` in the end-to-end phase.

## Initial messages

- `DisplayDescriptor`: stable display identity, friendly name, pixel dimensions, scale, and clockwise rotation.
- `PointerEventMessage`: unique event ID, session identity, monotonic sequence, normalized coordinate, kind, send time, and TTL.
- `PointerAcknowledgement`: event identity and receiver display time.
- `JoinRequest`: one-time pairing code, requested role, durable client-instance identity, and version.
- `SessionStateMessage`: session identity, approval state, receiver display, and expiry.

The default pointer TTL is 2,000 ms at the client. Structural validation currently permits a configurable maximum of 10,000 ms and a configurable future clock skew of 5,000 ms. Server configuration will constrain production values.

## Validation layers

1. JSON parsing rejects malformed or unexpected fields.
2. Contract validation rejects missing identities, invalid enums, invalid display metadata, non-finite/out-of-range coordinates, invalid TTLs, expired events, and implausible future timestamps.
3. The Phase 4 relay will validate live session state, the approved presenter identity, role permissions, replay/reordering, message size, and rate limits.

Pairing codes normalize case, whitespace, and hyphens. The accepted six-character alphabet excludes `0`, `O`, `1`, and `I` to reduce transcription errors. Pairing-code generation and server-side hashing are deferred to Phase 4.
