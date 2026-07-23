# Remote Pointer

Remote Pointer is a side-band Windows 11 pointer application. It exchanges only normalized pointer coordinates and session metadata through an internal relay; it does not capture screens or inject input.

Implementation proceeds one reviewed phase at a time. Phase 1 establishes the contracts and coordinate logic. Phase 2 adds a local Windows receiver-overlay prototype with monitor selection and test markers. Networking remains deferred to later phases.

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

Choose a monitor, select **Show receiver overlay**, and use the five preset marker buttons. The overlay should remain click-through and must not activate when clicking applications beneath it.

See [docs/architecture.md](docs/architecture.md) for component boundaries and phase status.
