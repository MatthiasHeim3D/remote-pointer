# Remote Pointer

Remote Pointer is a side-band Windows 11 pointer application. It exchanges only normalized pointer coordinates and session metadata through an internal relay; it does not capture screens or inject input.

Implementation proceeds one reviewed phase at a time. Phases 1–3 provide contracts and the local receiver/presenter prototypes. Phase 4 adds the independently deployable SignalR relay with receiver approval, role credentials, expiry, replay protection, and rate limiting. Client-to-relay wiring remains Phase 5 work.

## Build

Prerequisites:

- Windows 11
- .NET 10 SDK

```powershell
dotnet restore RemotePointer.sln
dotnet build RemotePointer.sln --configuration Release --no-restore
dotnet test RemotePointer.sln --configuration Release --no-build
```

Run the local overlay prototype:

```powershell
dotnet run --project src\RemotePointer.Client
```

Use **Receive pointers** to test the monitor overlay. Use **Point at another screen** to calibrate a target rectangle, lock it, and enter pointing mode. The receiver overlay remains click-through; the presenter target intentionally consumes clicks only while pointing mode is active.

Run the Phase 4 relay locally:

```powershell
dotnet run --project src\RemotePointer.Server --launch-profile https
```

The SignalR hub is available at `/hubs/pointer` and the health check at `https://localhost:7243/health`.

See [docs/architecture.md](docs/architecture.md) for component boundaries and phase status.
