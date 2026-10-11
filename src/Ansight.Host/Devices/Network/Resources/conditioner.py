"""Ansight's mitmdump addon. Conditions opaque TCP bytes without TLS interception.

Control is an atomically replaced JSON file owned by the host. No payloads are
logged or retained. The independent expiry/parent watchdog survives task loss.
"""
import asyncio
import json
import os
import random
import subprocess
import time
from pathlib import Path

from mitmproxy import ctx, version


class Conditioner:
    def __init__(self):
        self.path = Path(os.environ["ANSIGHT_NETWORK_CONFIG"])
        self.config = {}
        self.profile = {}
        self.revision = None
        self.next_send = {True: 0.0, False: 0.0}
        self.random = random.Random(0)
        self.flows = {}
        self.worker = None
        self.expiry_value = None
        self.expiry_deadline = 0
        self.next_identity_check = 0
        self.counters = {"connections": 0, "bypassedConnections": 0,
                         "uploadBytes": 0, "downloadBytes": 0, "blockedConnections": 0}

    def read_config(self):
        value = json.loads(self.path.read_text())
        now = time.time()
        expiry = value["expiresAtUnixSeconds"]
        if expiry != self.expiry_value:
            self.expiry_value = expiry
            self.expiry_deadline = time.monotonic() + max(0, expiry - now)
        if time.monotonic() >= self.expiry_deadline:
            return False
        try:
            os.kill(value["parentPid"], 0)
            if value.get("targetPid"):
                os.kill(value["targetPid"], 0)
        except ProcessLookupError:
            return False
        if value.get("targetStartedAtUnixSeconds") and time.monotonic() >= self.next_identity_check:
            # Prevent a restarted/reused PID from silently inheriting the route.
            self.next_identity_check = time.monotonic() + 1
            result = subprocess.run(["/bin/ps", "-p", str(value["targetPid"]), "-o", "lstart="],
                                    capture_output=True, text=True, timeout=1,
                                    env={**os.environ, "LC_ALL": "C"})
            started = time.mktime(time.strptime(result.stdout.strip(), "%a %b %d %H:%M:%S %Y"))
            if abs(started - value["targetStartedAtUnixSeconds"]) > 1:
                return False
        steps = value.get("steps", [])
        elapsed = (now - value["startedAtUnixSeconds"]) * 1000
        selected = len(steps)
        for index, step in enumerate(steps):
            elapsed -= step["durationMs"]
            if elapsed < 0:
                selected = index
                break
        profile = steps[selected]["profile"] if selected < len(steps) else value.get("profile", {})
        revision = (value["revision"], selected)
        if revision != self.revision:
            self.revision = revision
            self.next_send = {True: 0.0, False: 0.0}
            self.random.seed(profile.get("seed", 0))
        self.config = value
        self.profile = profile
        return True

    def bypass(self, flow):
        address = flow.server_conn.address
        if not address:
            return True
        host, port = address
        # Host control/session ports always bypass shaping, including encrypted
        # WebSockets. A test cannot remove this host-provided exemption.
        if port in self.config.get("bypassPorts", []):
            return True
        ports = self.config.get("destinationPorts", [])
        return bool(ports) and port not in ports

    async def running(self):
        if version.VERSION != "12.2.3":
            raise RuntimeError("Ansight conditioning requires the tested mitmproxy version 12.2.3.")
        if not self.read_config():
            ctx.master.shutdown()
            return
        self.write_status("ready")
        self.worker = asyncio.create_task(self.watch())

    async def watch(self):
        try:
            while self.read_config():
                if self.profile.get("blocked", False):
                    for flow in list(self.flows.values()):
                        if flow.live and not self.bypass(flow):
                            self.block(flow)
                self.write_status("ready")
                await asyncio.sleep(0.1)
        except (OSError, ValueError, KeyError, subprocess.TimeoutExpired):
            # Broken/lost configuration releases the routing process.
            pass
        finally:
            self.write_status("stopped")
            ctx.master.shutdown()

    def write_status(self, status):
        destination = self.path.with_suffix(".status.json")
        temporary = destination.with_suffix(".tmp")
        temporary.write_text(json.dumps({"status": status, "profile": self.profile,
                                        "revision": self.config.get("revision"),
                                        "stepIndex": self.revision[1] if self.revision else 0,
                                        "counters": self.counters}))
        temporary.replace(destination)

    def block(self, flow):
        if flow.metadata.get("ansight_blocked"):
            return
        flow.metadata["ansight_blocked"] = True
        self.counters["blockedConnections"] += 1
        # Flow.kill() only marks raw TCP flows in mitmproxy 12.2.3. Close the
        # actual transports as well. This adapter is intentionally version-pinned
        # and covered by real socket tests (including already-open connections).
        proxy = ctx.master.addons.get("proxyserver")
        handler = proxy.connections.get(flow.client_conn.id)
        if handler:
            for transport in list(handler.transports.values()):
                if transport.writer:
                    transport.writer.close()
                if transport.handler:
                    transport.handler.cancel("Ansight network conditioning")
        if flow.killable:
            flow.kill()

    def tcp_start(self, flow):
        self.flows[flow.id] = flow
        self.counters["connections"] += 1
        flow.metadata["ansight_directions"] = set()
        if self.bypass(flow):
            self.counters["bypassedConnections"] += 1
        elif self.profile.get("blocked", False):
            self.block(flow)

    async def tcp_message(self, flow):
        message = flow.messages[-1]
        # TCPLayer keeps its current message reference until this hook returns.
        # Dropping the history prevents transfer-sized capture buffers.
        flow.messages.clear()
        if self.bypass(flow):
            return
        if flow.metadata.get("ansight_blocked"):
            message.content = b""
            return
        direction = message.from_client
        first = direction not in flow.metadata["ansight_directions"]
        flow.metadata["ansight_directions"].add(direction)
        while flow.live:
            profile = self.profile
            revision = self.revision
            if profile.get("blocked", False):
                message.content = b""
                self.block(flow)
                return
            if profile.get("stall", False):
                await asyncio.sleep(0.05)
                continue
            now = time.monotonic()
            rate = profile.get("uploadKbps" if direction else "downloadKbps", 0)
            delay = max(0, profile.get("latencyMs", 0) + self.random.uniform(
                -profile.get("jitterMs", 0), profile.get("jitterMs", 0))) / 1000 if first else 0
            finish = max(now, self.next_send[direction]) + (len(message.content) * 8 / (rate * 1000) if rate else 0)
            self.next_send[direction] = finish
            deadline = finish + delay
            while time.monotonic() < deadline and revision == self.revision:
                await asyncio.sleep(min(0.05, max(0, deadline - time.monotonic())))
            if revision != self.revision:
                continue
            self.counters["uploadBytes" if direction else "downloadBytes"] += len(message.content)
            return

        message.content = b""

    def tcp_end(self, flow):
        self.flows.pop(flow.id, None)
        flow.messages.clear()

    tcp_error = tcp_end

    def udp_message(self, flow):
        # UDP/QUIC pass through unchanged and are explicitly outside this backend.
        flow.messages.clear()

    def done(self):
        if self.worker:
            self.worker.cancel()


addons = [Conditioner()]
