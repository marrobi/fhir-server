"""Minimal webhook receiver for local FHIR subscription testing.

Records each notification it receives. GET /notifications returns them as a JSON array;
DELETE /notifications clears them. Set FAIL_FIRST=n to fail the first n POSTs with HTTP 500.
"""
import json
import os
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

_lock = threading.Lock()
_notifications = []
_fail_remaining = int(os.environ.get("FAIL_FIRST", "0"))


class Handler(BaseHTTPRequestHandler):
    def do_POST(self):
        global _fail_remaining
        body = self.rfile.read(int(self.headers.get("Content-Length", "0"))).decode("utf-8")
        with _lock:
            if self.path.startswith("/fail") or _fail_remaining > 0:
                _fail_remaining = max(0, _fail_remaining - 1)
                self.send_response(500)
                self.end_headers()
                return
            _notifications.append({
                "path": self.path,
                "contentType": self.headers.get("Content-Type"),
                "authorization": self.headers.get("Authorization"),
                "body": json.loads(body) if body.startswith("{") else body,
            })
        self.send_response(200)
        self.end_headers()

    def do_GET(self):
        with _lock:
            payload = json.dumps(_notifications).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(payload)

    def do_DELETE(self):
        with _lock:
            _notifications.clear()
        self.send_response(204)
        self.end_headers()

    def log_message(self, format, *args):
        print("%s %s" % (self.command, self.path), flush=True)


if __name__ == "__main__":
    ThreadingHTTPServer(("0.0.0.0", int(os.environ.get("PORT", "8080"))), Handler).serve_forever()
