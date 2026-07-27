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
REMOTEPOINTER_SERVER_PASSWORD=change-this-to-your-own
```

## The server password

The server password is the relay's front door. Every client presents it to open its connection, and one that does not hold it is refused at the handshake: it never reaches the hub, so it can neither publish itself nor list or reach anyone, whatever room it names. Set it in `.env` as `REMOTEPOINTER_SERVER_PASSWORD`; the relay reads it as `Access__ServerPassword` and refuses to start on anything shorter than 8 characters.

The password itself never crosses the network. The relay derives a key from it with PBKDF2-SHA256 at startup, the client derives the same key from what the user typed, and the relay compares the two in constant time. The client stores only that derived key, under DPAPI, and can never show the password back.

Leaving `Access__ServerPassword` unset runs the relay open: anyone who can reach the address becomes a client. That is meant for local development, and the client shows a warning when it finds itself on such a relay.

Choose the password with the same care as one for a video meeting. Everyone holding it sees every published name and picture in every room, and changing it means telling everyone and restarting the relay.

## Rooms

A room is a plain name that scopes the host directory. Clients see each other when they are in the same room, and they type the name into Settings, where it is stored and shown back in the clear. Rooms are created and dropped implicitly — an unused one simply has no members, and there is nothing to administer.

A room is not an access control. Everyone reaching the relay has already presented the server password, and any of them can type any room name. Rooms separate teams from each other's clutter; the password is the boundary that matters.

Room names ignore case and surrounding spaces, so `Engineering` and `engineering` are one room. A client that names nothing lands in `general`.

## The host directory

Hosts publish themselves in the relay directory, which is how the desktop client finds them, and it is the only route into a session. There is no switch to turn it off: a relay that published nothing would accept no join request and could serve nobody. Each host still controls its own visibility and can hide itself at any time.

Being listed does not grant access — every direct join still requires host approval. It does mean anyone holding the server password can list the hosts published in any room they care to name.

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

`Invoke-RestMethod https://pointer.internal.example/version` reports the deployed relay build alongside the `remote-pointer-relay` product id. The client's connection test requires both — a host that only answers `/health` is rejected — and shows the version under the server address in its settings, which is the quickest way to confirm which build a user is actually talking to.

## Routine operation

```powershell
docker compose logs --tail 100
git pull
docker compose up -d --build
docker compose down
```

The relay image is built locally from this repository (`compose.yaml` uses a `build:` context, not a registry image), so updating means refreshing the source and rebuilding with `--build`. There is no `docker compose pull` step for the relay; `docker compose up -d --build` still pulls a newer Caddy base image when one is available.

A prebuilt relay image is also published to GitHub Container Registry, but only when a release tag matching `v*` is pushed — not on every push to `main`. CI refuses to publish unless the tag is the one Nerdbank.GitVersioning expects for that commit and the commit is an ancestor of `main`. Each run publishes `ghcr.io/<owner>/remote-pointer-relay:<version>` and moves `:latest` to it. To run a published image instead of building, replace the relay `image:`/`build:` block in `compose.yaml` with that reference.

`docker compose down` preserves the named Caddy data volume. Do not add `--volumes` during normal maintenance: deleting that volume creates a new CA, after which every client installer must be rebuilt with the new public root. Restarting the relay intentionally ends active in-memory sessions, so users pair again.
