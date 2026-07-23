# Remote Pointer

Remote Pointer is a side-band Windows 11 pointer application. It exchanges only normalized pointer coordinates and session metadata through an internal relay; it does not capture screens or inject input.

Implementation proceeds one reviewed phase at a time. Phases 1–5 provide the contracts, desktop overlays, relay, and end-to-end workflow. Phase 6 hardens that workflow with production HTTPS refusal, DPAPI-protected crash recovery, stable structured audit events, safe error boundaries, dependency auditing, a threat model, and Authenticode signing configuration.

## Build

Prerequisites:

- Windows 11
- .NET 10 SDK

```powershell
dotnet restore RemotePointer.sln
dotnet build RemotePointer.sln --configuration Release --no-restore
dotnet test RemotePointer.sln --configuration Release --no-build
```

Start the local relay, then start two client processes:

```powershell
dotnet run --project src\RemotePointer.Server --launch-profile https
dotnet run --project src\RemotePointer.Client
dotnet run --project src\RemotePointer.Client
```

In the first client, use **Receive pointers**, select a monitor, and create a session. In the second, use **Point at another screen**, enter the pairing code, and request access. Approve the presenter in the receiver, then calibrate and enable pointing. The receiver overlay remains click-through; the presenter target consumes clicks only while pointing mode is active.

The SignalR hub is available at `/hubs/pointer` and the health check at `https://localhost:7243/health`.

See [docs/architecture.md](docs/architecture.md) for component boundaries and phase status.
