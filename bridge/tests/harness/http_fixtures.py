"""Local HTTP server standing in for freemidi.org and bitmidi.com.

Paths are prefixed with the site's host: `/freemidi.org/search?q=...`.
"""

from __future__ import annotations

import threading
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


@dataclass
class Response:
    status: int = 200
    body: bytes = b""


@dataclass
class Request:
    path: str
    headers: dict[str, str]


@dataclass
class FixtureSite:
    routes: dict[str, Response] = field(default_factory=dict)
    requests: list[Request] = field(default_factory=list)
    base_url: str = ""

    def reset(self) -> None:
        self.routes.clear()
        self.requests.clear()

    def serve(self, path: str, body: bytes | str, status: int = 200) -> None:
        raw = body.encode() if isinstance(body, str) else body
        self.routes[path] = Response(status, raw)

    def requested(self, path_prefix: str) -> list[Request]:
        return [r for r in self.requests if r.path.startswith(path_prefix)]


def start_fixture_server() -> tuple[FixtureSite, ThreadingHTTPServer]:
    site = FixtureSite()
    lock = threading.Lock()

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self) -> None:
            with lock:
                site.requests.append(Request(self.path, dict(self.headers.items())))
                response = site.routes.get(self.path) or site.routes.get(self.path.split("?")[0])
            if response is None:
                response = Response(404, b"not found")
            self.send_response(response.status)
            self.send_header("Content-Length", str(len(response.body)))
            self.end_headers()
            self.wfile.write(response.body)

        def log_message(self, *_args: object) -> None:
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    site.base_url = f"http://127.0.0.1:{server.server_address[1]}"
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return site, server
