# Architecture

## System boundary

Remote Pointer has three deployable or reusable components:

| Component | Responsibility | Must not do |
| --- | --- | --- |
| `RemotePointer.Client` | Presenter calibration/capture and receiver marker overlays | Capture screens or inject remote input |
| `RemotePointer.Server` | Validate, authorize, rate-limit, and relay transient pointer events | Access either user's desktop |
| `RemotePointer.Contracts` | Transport-neutral messages, coordinate math, JSON policy, and structural validation | Depend on WPF, SignalR, or ASP.NET Core |

Both clients will establish outbound HTTPS/WSS connections to the server. The server will place the approved presenter and receiver in a session-specific SignalR group. Peer-to-peer connectivity is deliberately excluded.

## Phase 1 design

The contracts assembly targets `net10.0` and contains no desktop or web framework references. Coordinates use small immutable value types based on `double`, allowing later callers to keep WPF-DIP and physical-pixel conversion at the platform boundary.

`CoordinateMapper.Normalize` uses a calibrated rectangle that can have a negative origin. It clamps its result to the inclusive normalized range. `CoordinateMapper.Denormalize` requires an already valid normalized coordinate; malformed network input must be rejected rather than silently corrected.

Structural contract validation returns stable error codes and does not throw for untrusted messages. Session membership, role authorization, expiry, and rate limits require server state and remain server responsibilities for Phase 4. `SequenceNumberTracker` provides a bounded replay/reordering window that the server can maintain per approved presenter session.

## Phase 2 design

The receiver prototype enumerates monitor handles and physical bounds through `EnumDisplayMonitors` and `GetMonitorInfo`. Each monitor retains its native virtual-desktop origin, including negative coordinates. Effective monitor DPI is recorded as a scale factor, and `DisplayCoordinateMapper` is the only client service that converts physical pixels to WPF device-independent units.

`ReceiverOverlayWindow` is a transparent top-level WPF window positioned with `SetWindowPos` in physical pixels. After handle creation it applies `WS_EX_LAYERED`, `WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE`, and `WS_EX_TOOLWINDOW`. Native hit testing returns `HTTRANSPARENT`, and mouse activation returns `MA_NOACTIVATE`.

Both the control window and receiver overlay listen for `WM_DISPLAYCHANGE`. Resolution or placement changes cause the selected display to be enumerated again and the overlay to be repositioned. If its display ID no longer exists, the overlay closes and the main window presents an explicit disconnection error.

Markers are local-only Phase 2 test visuals. Up to five expanding rings can be visible simultaneously, each fading after approximately 900 ms. Their centers are mapped from normalized coordinates into the overlay's current WPF client size.

## Phase 3 design

`TargetRegionService` owns the presenter state machine: inactive, calibrating, ready, and pointing. A calibrated rectangle is retained only in memory for the current process session. Recalibration can preserve the last rectangle, while Reset restores a recommended rectangle matching the receiver's expected aspect ratio.

`TargetRegionWindow` is interactive during calibration. Its header moves the window, a resize thumb changes its dimensions, and an optional ratio lock constrains resizing. The current dimensions, ratio, and relative difference from the receiver shape are displayed continuously. Locking a difference over 2% requires an explicit Allow mismatch choice.

Pointing mode reuses the exact calibrated top-level window bounds with transparent content and a subtle border. Because the pointer window exists only over the target rectangle, normal clicks outside it are unaffected. Left clicks inside are handled by WPF, rendered as local ripples, normalized through the shared `CoordinateMapper`, and never forwarded or injected into the underlying application.

The control window registers `Ctrl+Alt+P` with `RegisterHotKey`; no keyboard or mouse hook is installed. Escape is handled by the focused target window and immediately closes pointing mode. Inactive mode closes the target window entirely.

## Dependency direction

```text
RemotePointer.Client ----+
                         +--> RemotePointer.Contracts
RemotePointer.Server ----+
```

No reference is permitted from contracts back to either host. Client and server tests reference only the corresponding production project and contracts as needed.

## Phase status

- Phase 1: implemented.
- Phase 2: implemented; manual mixed-DPI and display-disconnection verification is required on target hardware.
- Phase 3: implemented; click-consumption and mixed-DPI calibration require manual verification on target hardware.
- Phases 4–7: not yet implemented.
