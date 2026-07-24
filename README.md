# Remote Pointer

Add a shared, on-screen pointer to any remote-desktop or screen-sharing session, so you can point at and annotate what's on someone else's Windows screen while you talk them through it.

Remote Pointer is **not** a remote-desktop tool and doesn't replace one. It doesn't show you the other screen or let you control their PC — you already see their screen through whatever tool you use (Teams, Zoom, RDP, a screen-share, and so on). What Remote Pointer adds on top is a **temporary** pointer: one or more people draw gestures and short text notes that appear on the other person's real screen, like a laser pointer you can use remotely. Because it drives the receiver's own screen, it works alongside any remote-desktop or screen-sharing tool.

It only ever sends the *position* of your gestures and the text you type — it never captures or streams the screen, records anything, or moves the other person's mouse or keyboard.

It is built for small, trusted networks (an office LAN or VPN) and runs entirely on infrastructure you host — there is no cloud service and no peer-to-peer connection.

Good for walking a colleague through an app, pointing out details during a screen-share, or remote pair-troubleshooting.

## How it works

Remote Pointer has two roles and a small server that connects them:

- **Receiver** — the person whose screen is being pointed at. A transparent, click-through overlay shows the incoming markers while they keep using their PC normally underneath.
- **Sender** — the person doing the pointing. In their remote-desktop or screen-share view, they line up a target area over the receiver's screen, then draw. Their gestures show up on the receiver's real screen (and so are visible back in the shared view).
- **Relay** — a small self-hosted server that both sides connect to over HTTPS. It validates and forwards gestures; it never touches either desktop.

Every sender must be **individually approved** by the receiver, and the receiver can disconnect everyone at any time. A receiver accepts a limited number of senders at once (two by default).

## Using it

1. On the receiving PC, open Remote Pointer and choose **Available** to become discoverable.
2. On each sending PC, pick that receiver from the list and request access.
3. The receiver approves each request.
4. Senders line up their target area over the receiver's screen in their remote-desktop view, then turn on pointing (or press **Ctrl+Alt+P**).

### Pointer controls

While pointing is active:

| Gesture | Draws |
| --- | --- |
| Left-click | Highlight a spot |
| Left-drag | Freehand path |
| Shift + left-drag | Straight line |
| Shift + left-click | Text note (press **Enter** to place it) |
| Right-drag | Box |
| Shift + right-drag | Circle, grown out from its center |
| **Escape** | Cancels the current gesture |
| **H** | Shows or hides the on-screen controls help |

Everything you draw fades on its own after a couple of seconds — nothing is saved on either side. The help panel opens the first time you point and can be reopened any time with **H**; turn off *Show usage hints* in Settings to hide its badge.

## Requirements

- **To use it:** Windows 11. The client is self-contained, so no separate .NET install is needed.
- **To see the other screen:** any remote-desktop or screen-sharing tool (Teams, Zoom, RDP, and so on). Remote Pointer adds the pointer on top; it does not provide the screen view itself.
- **To run the relay:** a machine with Docker, reachable over HTTPS from everyone taking part.

## Installing

Remote Pointer is distributed as a per-user Windows installer that needs no administrator rights and installs only for the current account. There is no public download — whoever runs your relay builds and shares the installer. On first launch, the client asks for your relay's HTTPS address.

- Set up the relay server → [Server deployment](docs/server-deployment.md)
- Build and install the client → [Client deployment](docs/deployment.md)

## Building from source

Prerequisites: Windows 11 and the .NET 10 SDK.

```powershell
dotnet build RemotePointer.sln --configuration Release
dotnet test RemotePointer.sln --configuration Release
```

To try the whole thing on one machine — a local relay plus two client windows so you can play both roles — run:

```powershell
.\build\Start-Development.ps1
```

It starts a local HTTPS relay and two clients and shuts everything down when both clients close.

## Documentation

- [Architecture](docs/architecture.md) — components, data flow, and design decisions
- [Protocol](docs/protocol.md) — message format and coordinate math
- [Security](docs/security.md) and [Threat model](docs/threat-model.md) — controls and trust boundaries
- [Client deployment](docs/deployment.md) and [Server deployment](docs/server-deployment.md) — building the installer and hosting the relay
- [Dependencies](docs/dependencies.md) and [Test matrix](docs/test-matrix.md)

## Development

Remote Pointer was developed with the use of AI coding agents.

## License

Released under the [MIT License](LICENSE).
