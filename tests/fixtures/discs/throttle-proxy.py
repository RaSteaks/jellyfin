#!/usr/bin/env python3
"""Loopback-only HTTP test proxy with a shared media bandwidth schedule.

Example: --upstream http://127.0.0.1:18103 --port 18104 --phases 20:30,2:60,15
A phase is Mbps:seconds; the last Mbps value continues until the proxy stops.
Only media segments and Jellyfin's bitrate probe are throttled. API/assets and
WebSockets pass through. No authentication credentials are stored or logged.
"""
import argparse
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import selectors
import socket
import ssl
import threading
import time
from urllib.parse import urlsplit


class MediaLimiter:
    def __init__(self, phases):
        self.phases = phases
        self.started = None
        self.available = 0.0
        self.lock = threading.Lock()

    def consume(self, size):
        with self.lock:
            now = time.monotonic()
            if self.started is None:
                self.started = now
                print('Media schedule started', flush=True)
            slot = max(now, self.available)
            elapsed = slot - self.started
            for rate, duration in self.phases:
                if elapsed < duration:
                    if rate == 0:
                        slot += duration - elapsed
                        elapsed = 0
                        continue
                    break
                elapsed -= duration
            duration = size * 8 / (rate * 1_000_000)
            # Keep the reserved clock across reads so scheduler oversleep does not
            # reduce the configured rate on every chunk. Allow at most one chunk burst.
            self.available = max(self.available, slot - duration) + duration
            delay = max(0, self.available - now)
        time.sleep(delay)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--upstream', default='http://127.0.0.1:18103')
    parser.add_argument('--port', type=int, default=18104)
    parser.add_argument('--phases', default='20:30,2:60,15')
    args = parser.parse_args()
    upstream = urlsplit(args.upstream)
    if upstream.scheme not in ('http', 'https') or upstream.username or not upstream.hostname:
        parser.error('Use an HTTP(S) upstream without embedded credentials')
    phases = []
    for entry in args.phases.split(','):
        parts = entry.split(':')
        rate = float(parts[0])
        duration = float(parts[1]) if len(parts) == 2 else float('inf')
        if rate < 0 or duration <= 0 or (rate == 0 and duration == float('inf')):
            parser.error('Rates must be nonnegative; an outage needs a finite duration')
        phases.append((rate, duration))
    if phases[-1][1] != float('inf'):
        parser.error('End the schedule with a bitrate without a duration')
    limiter = MediaLimiter(phases)
    port = upstream.port or (443 if upstream.scheme == 'https' else 80)

    class Handler(BaseHTTPRequestHandler):
        protocol_version = 'HTTP/1.1'

        def log_message(self, message, *values):
            # Query strings contain Jellyfin access tokens.
            print(self.command, urlsplit(self.path).path, flush=True)

        def relay(self):
            path = upstream.path.rstrip('/') + self.path
            if self.headers.get('Upgrade', '').lower() == 'websocket':
                self.tunnel(path)
                return
            cls = http.client.HTTPSConnection if upstream.scheme == 'https' else http.client.HTTPConnection
            conn = cls(upstream.hostname, port, timeout=120)
            headers = {k: v for k, v in self.headers.items() if k.lower() not in ('host', 'connection', 'transfer-encoding')}
            headers['Host'] = self.headers.get('Host', upstream.netloc)
            headers['Connection'] = 'close'
            body = self.rfile.read(int(self.headers.get('Content-Length', 0))) or None
            try:
                conn.request(self.command, path, body=body, headers=headers)
                response = conn.getresponse()
                self.send_response_only(response.status)
                for key, value in response.getheaders():
                    if key.lower() not in ('connection', 'transfer-encoding', 'keep-alive'):
                        self.send_header(key, value)
                self.send_header('Connection', 'close')
                self.end_headers()
                route = urlsplit(self.path).path.lower()
                limited = '/hls' in route or route.endswith('/playback/bitratetest')
                if self.command != 'HEAD':
                    while chunk := response.read(16 * 1024):
                        if limited:
                            limiter.consume(len(chunk))
                        self.wfile.write(chunk)
                self.wfile.flush()
            except (BrokenPipeError, ConnectionResetError):
                # A quality switch/seek cancels the old media request.
                pass
            finally:
                conn.close()
                self.close_connection = True

        def tunnel(self, path):
            conn = socket.create_connection((upstream.hostname, port), timeout=30)
            if upstream.scheme == 'https':
                conn = ssl.create_default_context().wrap_socket(conn, server_hostname=upstream.hostname)
            headers = ''.join(f'{key}: {value}\r\n' for key, value in self.headers.items() if key.lower() != 'host')
            host = self.headers.get('Host', upstream.netloc)
            conn.sendall((f'{self.command} {path} HTTP/1.1\r\nHost: {host}\r\n{headers}\r\n').encode('latin-1'))
            try:
                with selectors.DefaultSelector() as selector:
                    selector.register(self.connection, selectors.EVENT_READ, conn)
                    selector.register(conn, selectors.EVENT_READ, self.connection)
                    while True:
                        for key, _ in selector.select(timeout=30):
                            data = key.fileobj.recv(64 * 1024)
                            if not data:
                                return
                            key.data.sendall(data)
            except (BrokenPipeError, ConnectionResetError):
                pass
            finally:
                conn.close()
                self.close_connection = True

        do_GET = relay
        do_HEAD = relay
        do_POST = relay
        do_DELETE = relay
        do_PUT = relay
        do_OPTIONS = relay

    server = ThreadingHTTPServer(('127.0.0.1', args.port), Handler)
    server.daemon_threads = True
    print(f'Isolated media proxy: http://127.0.0.1:{args.port}; phases: {args.phases}', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == '__main__':
    main()
