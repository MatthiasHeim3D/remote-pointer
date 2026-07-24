# Relay deployment

The small-network deployment is two containers: Caddy exposes HTTPS on port 443 and the Remote Pointer relay is reachable only from Caddy on Docker's private network. Caddy creates and renews the leaf certificate from a persistent local CA; there is no PFX file or certificate password to manage.

## Start the server

Choose one DNS hostname that resolves to the Docker host from both the office network and VPN. It must be the same hostname built into the client installer.

```powershell
Set-Location .\deploy\server
Copy-Item .env.example .env
```

Edit `.env`, then start the stack:

```text
REMOTEPOINTER_HOSTNAME=pointer.internal.example
REMOTEPOINTER_RECEIVER_DISCOVERY_ENABLED=true
```

Receiver discovery is enabled by default so receivers can explicitly publish themselves in the relay directory. Set `REMOTEPOINTER_RECEIVER_DISCOVERY_ENABLED=false` to disable the directory. Every direct join still requires receiver approval. Pairing-code joins remain available at the relay protocol level for compatibility but are not exposed by the desktop client.

```powershell
docker compose up -d --build
docker compose ps
```

Only TCP 443 needs to be reachable from client machines. Do not publish relay port 8080; it is deliberately plaintext only inside the private Compose network.

Validate HTTPS from the server after Caddy starts:

```powershell
curl.exe -k https://pointer.internal.example/health
```

`-k` is appropriate only for this initial server-side check, before the new root is trusted. The application itself has no certificate-validation bypass.

## Export the public root for the installer

```powershell
docker compose cp caddy:/data/caddy/pki/authorities/local/root.crt .\relay-root.crt
```

Copy only `relay-root.crt` to the build machine. Never copy or distribute `root.key` or the `caddy_data` volume. Build the client installer using the exact HTTPS hostname and this root as described in [deployment.md](deployment.md).

On a client where the installer has run, this should succeed without `-k`:

```powershell
Invoke-RestMethod https://pointer.internal.example/health
```

## Routine operation

```powershell
docker compose logs --tail 100
docker compose pull
docker compose up -d --build
docker compose down
```

`docker compose down` preserves the named Caddy data volume. Do not add `--volumes` during normal maintenance: deleting that volume creates a new CA, after which every client installer must be rebuilt with the new public root. Restarting the relay intentionally ends active in-memory sessions, so users pair again.
