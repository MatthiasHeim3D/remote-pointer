# Remote Pointer

Remote Pointer is a side-band Windows 11 pointer application. It exchanges only normalized pointer coordinates and session metadata through an internal relay; it does not capture screens or inject input.

Implementation proceeds one reviewed phase at a time. Phase 1 establishes contracts and coordinate logic. Phase 2 adds the local receiver overlay. Phase 3 adds presenter calibration, click capture, aspect-ratio guidance, local ripple feedback, and the `Ctrl+Alt+P` global toggle. Networking remains deferred to Phase 4 and later.

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

See [docs/architecture.md](docs/architecture.md) for component boundaries and phase status.
