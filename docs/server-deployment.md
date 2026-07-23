# Relay server deployment guide

## Production topology

Deploy the relay on an internal server or container platform behind an approved DNS name. Clients connect only to `https://<relay-fqdn>/hubs/pointer`; monitoring uses `https://<relay-fqdn>/health`. The current session store is in memory, so a relay restart ends active sessions and horizontal scaling requires session affinity. Do not deploy multiple independent replicas as if they shared sessions.

## Container build

From the repository root:

```powershell
docker build -f src/RemotePointer.Server/Dockerfile -t registry.internal/remote-pointer-relay:1.0.0 .
docker push registry.internal/remote-pointer-relay:1.0.0
```

`deploy/server/compose.yaml` is a hardened single-node example. Before starting it:

1. Put the organization-issued PFX at `C:\ProgramData\RemotePointer\tls\relay.pfx` or change the bind mount.
2. Grant the container runtime read access without granting interactive users access.
3. Set `REMOTEPOINTER_TLS_PASSWORD` through the platform secret store, not a checked-in environment file.
4. Replace the image tag with an immutable digest in production.

```powershell
$env:REMOTEPOINTER_TLS_PASSWORD = '<injected-by-secret-store>'
docker compose -f deploy/server/compose.yaml up -d
```

The container listens on unprivileged port 8443 as a non-root user; the host publishes it as TCP 443. It has a read-only root filesystem, dropped Linux capabilities, and a temporary `/tmp` mount.

## Framework-dependent deployment

For a managed Windows/Linux host with .NET 10 installed:

```powershell
dotnet publish src\RemotePointer.Server `
  --configuration Release `
  --no-restore `
  --output artifacts\publish\server
```

Run it under the organization's process supervisor using a dedicated, non-interactive identity. Do not run it from an administrator account. Set:

```text
ASPNETCORE_ENVIRONMENT=Production
Kestrel__Endpoints__Https__Url=https://0.0.0.0:443
Kestrel__Endpoints__Https__Certificate__Path=<protected path>/relay.pfx
Kestrel__Endpoints__Https__Certificate__Password=<secret reference>
```

Environment variables override JSON using ASP.NET Core's double-underscore convention. Useful policy settings include:

```text
Sessions__PairingCodeLifetimeMinutes=10
Sessions__MaximumSessionHours=8
RateLimits__EventsPerSecond=20
RateLimits__BurstSize=30
```

## Network and TLS

- Allow inbound TCP 443 only from approved client networks or the internal reverse proxy.
- Allow WebSocket upgrade requests to `/hubs/pointer`.
- Expose `/health` only to approved monitors and load balancers.
- Refuse plaintext HTTP; the production application returns an error instead of redirecting it.
- Use an organization PKI certificate whose SAN contains the client-configured FQDN.
- Rotate the certificate before expiry and verify both health and a real SignalR session afterward.
- Never store the PFX password in source, image layers, compose files, or ordinary logs.

## Logging and retention

Send structured stdout/application logs to the approved central collector. Default logs include lifecycle, approval, rejection, validation, connection, error, and aggregate pointer-count events; they exclude coordinates and credentials. Apply organization access and retention policy to the collector. Alert on repeated authorization failures, rate-limit rejections, health failures, certificate expiry, and unexpected process restarts.

See `docs/operations-runbook.md` for deployment, rollback, incident, and recovery procedures.
