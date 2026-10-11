# Network conditioning for tests

Network conditioning runs in the **resident Ansight host**. It is exposed through
`ansight.device.network` in the TypeScript test SDK and `ansight device network`
in the CLI. The application SDK's `Ansight.Connectivity` package records what the
OS actually observes; it does not install or control the proxy.

## Host setup

The initial backend supports **macOS hosts**, **iOS Simulator** and **Android
Emulator**, using mitmproxy **12.2.3**. It does not support physical devices or
Windows/Linux hosts. The addon deliberately pins this version because closing
raw TCP connections uses a version-specific mitmproxy transport adapter.

```sh
brew install --cask mitmproxy
mitmdump --version
```

Verify that the installed version is 12.2.3. `ANSIGHT_MITMDUMP_PATH` can select an
absolute executable path. The host also searches PATH and standard Homebrew
locations. No Python package installation is required for the standalone cask.

The first local-capture attempt installs mitmproxy's redirector. macOS may require
approval under **System Settings → General → Login Items & Extensions → Network
Extensions**. `systemextensionsctl list` reports `activated waiting for user`
until that approval is complete. A timed-out start cleans up its proxy and must
be retried after approval; it does not keep a hidden conditioning lease running.

Build this checkout's CLI before using the new API:

```sh
dotnet build src/Ansight.Cli/Ansight.Cli.csproj
# Use this executable for the commands below, or install your local CLI build:
./src/Ansight.Cli/bin/Debug/net10.0/ansight device network --help
```

The resident host must also run this build. An older resident host cannot execute
the new operations. Start it using the built executable's `host run` command in
your normal development-host configuration.

## Scope and behavior

| Item | Behavior |
| --- | --- |
| iOS Simulator | Resolves the exact UDID + bundle ID to one running app PID. |
| Android Emulator | Resolves the exact emulator serial's console listener to a QEMU PID. All apps in that emulator share the scope. |
| Other apps/devices | Not included in the PID selector. |
| Ansight control | Configured host discovery and WebSocket ports always bypass shaping. |
| TLS | Opaque encrypted passthrough; no CA is installed or trusted. |
| Bandwidth | Aggregate upload/download TCP payload pacing, in decimal kilobits/s; `0` means unlimited. |
| Latency/jitter | Delays the first TCP payload in each direction of each connection, including a TLS handshake. This is not packet-level RTT. |
| `stall` | Holds TCP payloads until conditions change or the lease ends. Use bounded test transfers. |
| `blocked` | Closes new and existing targeted TCP connections. Does not switch the radio or make OS connectivity report offline. |
| Unsupported | UDP/QUIC shaping, packet loss, DNS failure injection, RSSI, transport switching and captive-portal simulation. |

Start conditioning before opening the connections under test. Existing sockets
that were established before local capture may not be redirected. OS services
and background networking attributed to a different process may also bypass an
iOS app PID. Check both application behavior and the lease's byte counters.

`capabilities()` is a preflight, not routing proof. A successful `set()` means
mitmdump loaded the requested profile. Neither proves that a particular request
used that process or TCP. The harness deliberately uses a new in-process
HTTP/1.1 connection for each probe, avoiding HTTP/3/QUIC.

Only **one conditioning lease per host** is allowed. A task cannot modify another
task's lease. Updates require the returned `leaseId`, keep its original expiry
and port filter, and replace the entire profile. `{}` restores unrestricted TCP
pacing while keeping routing active; `reset()` removes routing and closes
proxied sockets. Conditions are not a persistent device setting.

Leases last 1–300 seconds (default 120). Their lifetime starts after local capture
is ready. A host timer and a separate proxy watchdog bound the lifetime. Task
completion, failure, cancellation, host shutdown, target exit or lost host process
also release the route. Recovery requires the app to reconnect closed sockets.
A scenario advances inside the proxy, so it does not depend on further app calls.

## TypeScript API

Refresh the workspace's generated declarations from this CLI build when adopting
the new API. Preserve any local custom declarations before replacing support files.

```ts
const available = await ansight.device.network.capabilities();
expect(available.available, { id: "network-conditioning-available" }).toBe(true);

const lease = await ansight.device.network.set({
  profile: { downloadKbps: 512, uploadKbps: 128, latencyMs: 150, jitterMs: 30, seed: 42 },
  destinationPorts: [443], // optional; empty/omitted means all TCP except control ports
  durationMs: 60_000
});
try {
  // Await UI actions serially here, and assert application behavior.
  const status = await ansight.device.network.get({ leaseId: lease.leaseId });
  // status.profile, stepIndex, expiresAtUtc, counters and scope are inspectable.
  await ansight.device.network.set({ leaseId: lease.leaseId, profile: { blocked: true } });
  // Assert the app's error/retry behavior here.
} finally {
  await ansight.device.network.reset({ leaseId: lease.leaseId });
}
```

For an autonomous timed sequence:

```ts
const scenario = await ansight.device.network.runScenario({
  destinationPorts: [443],
  steps: [
    { durationMs: 5_000, profile: { downloadKbps: 256 } },
    { durationMs: 3_000, profile: { blocked: true } },
    { durationMs: 10_000, profile: {} }
  ]
});
// Returns after starting. Perform serial UI actions while the proxy advances.
// It automatically resets at the end, or when this task exits.
```

Scenarios accept 1–50 steps, each at least 1 second, and at most 300 seconds total.
A fixed seed controls jitter draws; connection ordering can still differ between
runs. This is repeatable test input, not a deterministic simulation of a radio.

## CLI

```sh
ansight device network capabilities --session <session-id> --json
ansight device network set --session <session-id> \
  --profile '{"downloadKbps":512,"latencyMs":150}' --duration-ms 60000 --ports 443 --json
ansight device network get --lease <returned-lease-id> --json
ansight device network set --session <session-id> --lease <lease-id> --profile '{"blocked":true}' --json
ansight device network reset --lease <lease-id> --json
ansight device network scenario --session <session-id> \
  --steps '[{"durationMs":5000,"profile":{"stall":true}},{"durationMs":10000,"profile":{}}]' --json
```

Without a live session, supply `--device-id <UDID|emulator-5554> --app-id <bundle-id>`.
`get` and `reset` require only a lease ID, so cleanup works after a session drops.
CLI-created leases expire automatically, but remain active after the initiating
CLI command returns. Reset them when finished. CLI commands cannot take ownership
of a task's lease.

## Capture evidence

Session-bound operations record `network.conditioning.applied`, `changed`, `step`
and cleanup events with lease ID, backend, scope, target, expiry, active profile
and counters. These are ordinary retained host session events, visible in the
local/web player's event timeline and in exported captures. Inspect them through
`ansight session timeline` or `ansight.session.getTimeline()` and correlate with
requests, logs, annotations and `ansight.connectivity` evidence.

These events describe **applied test conditions**. Connectivity snapshots describe
**observed OS state** and must not be fabricated from the profile. A TCP block can
cause request failures while the OS still reports validated Wi-Fi. Operations
started with only an explicit device/app target have no session in which to store
host evidence. Host events also cannot be retroactively inserted into an SDK-only
offline archive that was never connected to the host.

## Verification and harness

```sh
python3 -m unittest discover -s tests/network-conditioning -v
```

These loopback tests run real mitmdump processes and prove bandwidth/latency,
stall/recovery, new/existing connection blocking, port bypass, independent timed
steps, expiry, lost-parent cleanup and TLS passthrough with endpoint-only trust.
They do not need the macOS local-capture extension. The host/CLI test projects
also cover ownership, task cleanup, expiry, API binding and command forwarding.

The SDK repository's MAUI harness now contains a network probe and the
`network.conditioning` TypeScript task. See
`ansight-sdk/test-apps/core/NETWORK-CONDITIONING.md` for the fixture and run steps.
Process-scoped routing, throttling, blocking and recovery were verified on macOS
with the extension approved. Local capture bypasses loopback traffic: use the
Mac's LAN address for the fixture, including from a simulator or emulator.
A passing reverse-proxy test alone is not evidence that simulator routing works;
the harness also asserts observed proxy byte counters.

## Local player controls

The live simulator toolbar includes **Network**, beside Location. The panel offers presets, custom upload/download rates, initial delay and jitter, block/stall behavior, a TCP port filter, and automatic reset (up to five minutes). Traffic counters show what the conditioner has observed. Closing the panel leaves conditioning active and shows a compact countdown with Open and Reset controls, so the app can be exercised while shaping continues. Explicit Reset or expiry restores networking; closing the browser still relies on the host's expiry. The panel uses the same host service and ownership rules as the CLI and TypeScript SDK through JSON POST routes at `api/network/capabilities`, `set`, `get`, and `reset`.
