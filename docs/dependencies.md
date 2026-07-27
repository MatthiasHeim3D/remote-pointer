# Dependency inventory

Inventory generated on 2026-07-23 with:

```powershell
dotnet list RemoteAnnotate.sln package --include-transitive
dotnet list RemoteAnnotate.sln package --vulnerable --include-transitive
```

## Production dependencies

| Component | Direct package/framework | Version | Purpose |
| --- | --- | ---: | --- |
| Contracts | .NET runtime | 10.0 | JSON contracts, validation, coordinate math |
| Windows client | `Microsoft.AspNetCore.SignalR.Client` | 10.0.10 | HTTPS/WSS relay transport and reconnect |
| Windows client | WPF and Windows Forms shared frameworks | .NET 10 | Desktop UI, overlays, and notification icon |
| Windows client | Windows DPAPI (`ProtectedData`) | .NET 10 | Current-user encryption of recovery credentials |
| Relay | ASP.NET Core shared framework | 10.0 | Kestrel, SignalR, health checks, configuration, logging |

The SignalR client resolves the matching 10.0.10 `Microsoft.AspNetCore.Connections`, HTTP connections, SignalR common/JSON, dependency injection, logging, options, features, and primitives packages. There are no third-party production packages and the contracts/server projects have no direct NuGet dependency.

## Test-only dependencies

| Package | Version | Purpose |
| --- | ---: | --- |
| `Microsoft.AspNetCore.Mvc.Testing` | 10.0.10 | In-memory relay hosting |
| `Microsoft.NET.Test.Sdk` | 17.14.1 | Test host |
| `xunit` | 2.9.3 | Test framework |
| `xunit.runner.visualstudio` | 3.1.5 | Test discovery/runner |

Test transitive dependencies include Microsoft TestPlatform/CodeCoverage 17.14.1, ASP.NET Core TestHost and Microsoft.Extensions 10.0.10, xUnit components/analyzers, and Newtonsoft.Json 13.0.3 used by the test platform. They are not shipped as application features.

## Build and packaging dependencies

| Package/tool | Version | Purpose |
| --- | ---: | --- |
| Inno Setup | 6.x | Build the x64 setup executable (admin-free per-user by default, all-users optional) |
| Docker Compose | Current supported release | Run the relay and Caddy HTTPS proxy |
| Caddy container | `caddy:2-alpine` | Terminate HTTPS and manage the small deployment's local CA |

Inno Setup runs only on the build machine. Caddy runs only on the Docker server. Neither is part of the Windows client payload.

Central version declarations live in `Directory.Packages.props`. Framework and package updates require the full Release build, test suite, vulnerability scan, and a review of protocol compatibility and this inventory.

## Audit result

The Phase 6 Release build runs .NET analyzers at the latest installed analysis level with warnings treated as errors. On 2026-07-23, `dotnet list package --vulnerable --include-transitive` reported no known vulnerable packages in any production or test project from the configured NuGet sources. Any future advisory blocks release until triaged and documented.
