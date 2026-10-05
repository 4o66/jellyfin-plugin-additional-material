"""A stand-in for VirusTotal's file lookup (GET /files/<sha256>), for tests/integration.sh.

Knows two files by content: b"clean tool" (no engine flags it) and b"bad tool" (12 engines do);
any other file is unknown (404). The key must be "test-key" (401 otherwise). GET /stats reports
what was asked (key tests apart), so a test can prove only fingerprints were sent and nothing was uploaded.

  python fake_virustotal.py [port] [host]      (default 8000, all addresses)

Standard library only.
"""

import hashlib
import http.server
import json
import sys

KNOWN = {
    hashlib.sha256(b"clean tool").hexdigest(): {"malicious": 0, "suspicious": 0, "harmless": 0, "undetected": 70},
    hashlib.sha256(b"bad tool").hexdigest(): {"malicious": 12, "suspicious": 1, "harmless": 0, "undetected": 57},
}
EICAR = "275a021bbfb6489e54d471899f7db9d1663fc695ec2fe2a2c4538aabf651fd0f"   # what the plugin's Test key asks for
STATS = {"lookups": [], "key_tests": 0, "other": []}


class Handler(http.server.BaseHTTPRequestHandler):
    def reply(self, code, body):
        data = json.dumps(body).encode()
        self.send_response(code)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):  # noqa: N802
        if self.path == "/stats":
            return self.reply(200, STATS)
        if not self.path.startswith("/files/"):
            STATS["other"].append(f"GET {self.path}")
            return self.reply(404, {"error": {"code": "NotFoundError"}})
        if self.headers.get("x-apikey") != "test-key":
            return self.reply(401, {"error": {"code": "WrongCredentialsError", "message": "Wrong API key"}})
        digest = self.path.rsplit("/", 1)[-1]
        if digest == EICAR:
            STATS["key_tests"] += 1
        else:
            STATS["lookups"].append(digest)
        if digest not in KNOWN:
            return self.reply(404, {"error": {"code": "NotFoundError", "message": f"File \"{digest}\" not found"}})
        return self.reply(200, {"data": {"attributes": {"last_analysis_stats": KNOWN[digest]}}})

    def do_POST(self):  # noqa: N802
        STATS["other"].append(f"POST {self.path}")
        self.reply(405, {"error": {"code": "NotAllowed"}})

    do_PUT = do_PATCH = do_DELETE = do_POST

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 8000
    host = sys.argv[2] if len(sys.argv) > 2 else "0.0.0.0"
    http.server.ThreadingHTTPServer((host, port), Handler).serve_forever()
