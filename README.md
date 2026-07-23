# Remote Pointer

Remote Pointer is a side-band Windows 11 pointer application. It exchanges only normalized pointer coordinates and session metadata through an internal relay; it does not capture screens or inject input.

Implementation proceeds one reviewed phase at a time. Phase 1 establishes the .NET 10 solution, shared contracts, coordinate logic, validation, serialization policy, and unit tests. Desktop overlays and networking are intentionally deferred to later phases.

## Build

Prerequisites:

- Windows 11
- .NET 10 SDK

```powershell
dotnet restore RemotePointer.sln
dotnet build RemotePointer.sln --configuration Release --no-restore
dotnet test RemotePointer.sln --configuration Release --no-build
```

See [docs/architecture.md](docs/architecture.md) for component boundaries and phase status.
