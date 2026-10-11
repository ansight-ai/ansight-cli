"""Real mitmdump integration tests; loopback-only, no CA trust or global proxy changes.
Run: python3 -m unittest discover -s tests/network-conditioning -v
"""
import concurrent.futures
import json
import os
from pathlib import Path
import shutil
import socket
import socketserver
import ssl
import subprocess
import tempfile
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
ADDON = ROOT / 'src/Ansight.Host/Devices/Network/Resources/conditioner.py'
MITMDUMP = os.environ.get('ANSIGHT_MITMDUMP_PATH') or shutil.which('mitmdump')

class Echo(socketserver.BaseRequestHandler):
    def handle(self):
        try:
            while data := self.request.recv(65536):
                self.request.sendall(data)
        except (OSError, ssl.SSLError):
            pass

class Server(socketserver.ThreadingTCPServer):
    daemon_threads = True
    allow_reuse_address = True

@unittest.skipUnless(MITMDUMP, 'Install mitmproxy first')
class ConditionerIntegration(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = Server(('127.0.0.1', 0), Echo)
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()

    def setUp(self):
        self.folder = tempfile.TemporaryDirectory(prefix='ansight-network-test-')
        self.path = Path(self.folder.name) / 'conditions.json'
        self.revision = 0
        self.config = {'parentPid': os.getpid(), 'expiresAtUnixSeconds': time.time() + 120,
                       'startedAtUnixSeconds': time.time(), 'bypassPorts': [], 'destinationPorts': []}
        self.port = self.free_port()
        self.log = open(Path(self.folder.name) / 'mitmdump.log', 'w+')
        self.proxy = None
        self.update({}, wait=False)
        self.addCleanup(self.cleanup_proxy)
        self.start_proxy()

    def start_proxy(self, upstream=None):
        port = upstream or self.server.server_address[1]
        self.proxy = subprocess.Popen([MITMDUMP, '--mode', f'reverse:tcp://127.0.0.1:{port}@127.0.0.1:{self.port}',
            '--ignore-hosts', '.*', '--set', 'show_ignored_hosts=true', '--set', 'flow_detail=0',
            '--set', 'termlog_verbosity=warn', '--set', f'confdir={self.folder.name}', '-s', str(ADDON)],
            env={**os.environ, 'ANSIGHT_NETWORK_CONFIG': str(self.path)}, stdout=self.log, stderr=self.log)
        self.wait_revision()

    def cleanup_proxy(self):
        if self.proxy and self.proxy.poll() is None:
            self.proxy.terminate()
            try: self.proxy.wait(timeout=5)
            except subprocess.TimeoutExpired:
                self.proxy.kill(); self.proxy.wait()
        if not self.log.closed: self.log.close()
        self.folder.cleanup()

    @staticmethod
    def free_port():
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            return sock.getsockname()[1]

    def state(self):
        return json.loads(self.path.with_suffix('.status.json').read_text())

    def wait_revision(self):
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            if self.proxy.poll() is not None:
                self.log.seek(0)
                self.fail(self.log.read())
            try:
                if self.state()['revision'] == self.revision:
                    return
            except (FileNotFoundError, KeyError, json.JSONDecodeError):
                pass
            time.sleep(.03)
        self.log.seek(0)
        self.fail('Timed out: ' + self.log.read())

    def update(self, profile, wait=True, **extra):
        self.revision += 1
        self.config.update(profile=profile, revision=self.revision, **extra)
        temp = self.path.with_suffix('.new')
        temp.write_text(json.dumps(self.config))
        temp.replace(self.path)
        if wait: self.wait_revision()

    def exchange(self, data=b'hello', tls=None):
        start = time.monotonic()
        with socket.create_connection(('127.0.0.1', self.port), timeout=10) as raw:
            sock = tls.wrap_socket(raw, server_hostname='localhost') if tls else raw
            try:
                sock.sendall(data)
                result = b''
                while len(result) < len(data):
                    chunk = sock.recv(len(data) - len(result))
                    if not chunk: break
                    result += chunk
                self.assertEqual(result, data)
            finally:
                if tls: sock.close()
        return time.monotonic() - start

    def test_bandwidth_and_latency(self):
        self.update({'downloadKbps': 256, 'latencyMs': 100})
        elapsed = self.exchange(b'x' * 32768)
        self.assertGreater(elapsed, 1.15)
        self.assertLess(elapsed, 5)

    def test_stall_then_live_recovery(self):
        self.update({'stall': True})
        with concurrent.futures.ThreadPoolExecutor() as pool:
            request = pool.submit(self.exchange)
            time.sleep(.4)
            self.assertFalse(request.done())
            self.update({})
            self.assertGreater(request.result(timeout=3), .4)

    def test_blocked_closes_tcp(self):
        self.update({'blocked': True})
        with socket.create_connection(('127.0.0.1', self.port), timeout=2) as sock:
            try:
                sock.sendall(b'blocked')
                self.assertEqual(sock.recv(10), b'')
            except ConnectionResetError:
                pass

    def test_blocking_existing_connection_then_recovery(self):
        with socket.create_connection(('127.0.0.1', self.port), timeout=2) as sock:
            sock.sendall(b'before')
            self.assertEqual(sock.recv(6), b'before')
            self.update({'blocked': True})
            try:
                self.assertEqual(sock.recv(10), b'')
            except ConnectionResetError:
                pass
        self.update({})
        self.exchange(b'recovered')

    def test_control_port_bypass(self):
        self.update({'stall': True}, bypassPorts=[self.server.server_address[1]])
        self.assertLess(self.exchange(), .5)
        time.sleep(.15)
        self.assertGreater(self.state()['counters']['bypassedConnections'], 0)

    def test_destination_filter(self):
        self.update({'stall': True}, destinationPorts=[1])
        self.assertLess(self.exchange(), .5)

    def test_scenario_advances_independent_of_client(self):
        self.update({}, startedAtUnixSeconds=time.time(), steps=[
            {'durationMs': 700, 'profile': {'stall': True}},
            {'durationMs': 2000, 'profile': {}}])
        elapsed = self.exchange()
        self.assertGreater(elapsed, .4)
        self.assertLess(elapsed, 2)
        self.assertEqual(self.state()['stepIndex'], 1)

    def test_expiry_exits_proxy(self):
        self.update({}, expiresAtUnixSeconds=time.time() + .5)
        self.assertEqual(self.proxy.wait(timeout=5), 0)

    def test_lost_parent_exits_proxy(self):
        self.update({}, wait=False, parentPid=2147483647)
        self.assertEqual(self.proxy.wait(timeout=5), 0)

    def test_lost_target_exits_proxy(self):
        self.update({}, wait=False, targetPid=2147483647)
        self.assertEqual(self.proxy.wait(timeout=5), 0)

    def test_corrupt_configuration_releases_route(self):
        self.path.write_text('{broken')
        self.assertEqual(self.proxy.wait(timeout=5), 0)

    def test_upload_rate_and_aggregate_download_budget(self):
        self.update({'uploadKbps': 256})
        self.assertGreater(self.exchange(b'x' * 32768), .95)
        self.update({'downloadKbps': 256})
        with concurrent.futures.ThreadPoolExecutor() as pool:
            started = time.monotonic()
            requests = [pool.submit(self.exchange, b'x' * 32768) for _ in range(2)]
            for request in requests: request.result(timeout=15)
            self.assertGreater(time.monotonic() - started, 1.95)

    def test_reused_target_identity_releases_route(self):
        self.update({}, wait=False, targetPid=os.getpid(), targetStartedAtUnixSeconds=1)
        self.assertEqual(self.proxy.wait(timeout=5), 0)

    def test_tls_passthrough_with_exact_certificate_trust(self):
        cert = Path(self.folder.name) / 'server.pem'
        key = Path(self.folder.name) / 'server.key'
        subprocess.run(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-days', '1',
            '-subj', '/CN=localhost', '-addext', 'subjectAltName=DNS:localhost',
            '-keyout', str(key), '-out', str(cert)], check=True, capture_output=True)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(cert, key)
        server = Server(('127.0.0.1', 0), Echo)
        server.socket = context.wrap_socket(server.socket, server_side=True)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        try:
            self.proxy.terminate(); self.proxy.wait(timeout=5)
            self.update({'latencyMs': 100}, wait=False)
            self.start_proxy(server.server_address[1])
            client = ssl.create_default_context(cafile=str(cert))
            # Trust only our endpoint certificate; TLS interception would fail.
            self.assertGreater(self.exchange(tls=client), .15)
        finally:
            server.shutdown(); server.server_close()

if __name__ == '__main__': unittest.main()
