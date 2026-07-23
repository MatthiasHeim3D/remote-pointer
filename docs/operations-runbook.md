# Remote Pointer operational runbook

## Ownership and release evidence

Assign named owners for the client package, relay service, PKI certificate, network policy, and audit retention. For every release retain:

- Source revision and three-part version.
- Dependency/vulnerability report.
- Release build and test logs.
- MSI SHA-256 hash, signer subject, timestamp, and signature verification output.
- WiX ICE validation output.
- Clean Windows 11 install/upgrade/uninstall log bundle.
- Relay image digest or server publish hash.
- Change approval and rollback version.

## Routine health checks

1. Request `GET https://<relay-fqdn>/health`; require HTTPS success.
2. Check relay process/container restart count and resource use.
3. Check certificate hostname, chain, and remaining lifetime.
4. Review authorization failures, rate-limit events, server errors, and reconnect spikes.
5. Run a synthetic receiver/presenter session after infrastructure or certificate changes.

Never use pointer coordinates, pairing codes, or recovery tokens as monitoring dimensions.

## Client rollout

1. Validate the signed MSI on a clean Windows 11 VM.
2. Deploy the ProgramData relay configuration to a pilot device group.
3. Deploy the MSI to the pilot group in system context.
4. Verify standard-user launch, connection, pairing, calibration, delivery, and termination.
5. Expand in rings while watching install failures and relay errors.
6. Stop rollout if signature, configuration, health, or functional checks fail.

## Relay deployment

1. Confirm a current backup of deployment configuration; session data is intentionally not persistent.
2. Verify certificate and secret mounts before changing the running workload.
3. Deploy the immutable image/publish output during the approved window.
4. Expect active sessions to end when the single in-memory relay restarts.
5. Verify `/health`, then complete an end-to-end synthetic session.
6. Monitor errors and reconnects for at least one normal session lifetime or the organization's standard observation window.

## Rollback

Client MSI downgrades are blocked. Build the last approved application payload as a new higher three-part version, sign it, rerun clean-VM acceptance, and deploy it as a major upgrade.

For the relay, redeploy the previous immutable image digest or publish artifact, restore its matching non-secret configuration, and run health plus a new synthetic session. Existing sessions cannot be restored across a relay restart.

## Certificate rotation

1. Issue a replacement certificate containing the production FQDN.
2. Stage it in the protected certificate path/secret store.
3. Restart or roll the relay during a communicated window.
4. Validate chain, hostname, expiry, health, WebSocket connection, and end-to-end delivery.
5. Remove the old certificate only after rollback time has elapsed.

## Incident response

### Relay unavailable

- Confirm DNS, TCP 443, certificate validity, health, and process state.
- Preserve structured logs before restart when possible.
- Restart/redeploy the approved artifact. Users must create new sessions if in-memory state was lost.

### Suspected token or pairing abuse

- Restart the relay to invalidate all in-memory sessions if immediate containment is required.
- Preserve security audit logs and deployment evidence.
- Check repeated join, authorization, and validation failures without collecting pointer coordinates.
- Rotate TLS or code-signing material only when evidence indicates it is affected.

### Client package trust failure

- Pause deployment and remove the package from assignment.
- Compare SHA-256 and Authenticode signer/timestamp with release evidence.
- Do not advise users to bypass SmartScreen, certificate validation, or application control.
- Rebuild and re-sign from a trusted build environment if integrity cannot be established.

## Offboarding and data retention

MSI uninstall removes Program Files content and shortcuts. It intentionally leaves:

- `%ProgramData%\RemotePointer\clientsettings.json`, because it is organization policy rather than application payload.
- `%LocalAppData%\RemotePointer\Logs`, for policy-controlled audit retention.
- `%LocalAppData%\RemotePointer\Sessions`, which may contain DPAPI-protected reconnect credentials.

Use a separately approved endpoint-retirement policy to remove retained files after their legal/security retention period. Do not add broad recursive deletion to the installer.
