"""Serves the fake sites for the README screenshots: each host name gets pages/<host>.html."""
import http.server
import pathlib
import ssl
import sys

PAGES = pathlib.Path(__file__).resolve().parent / "pages"


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        host = self.headers.get("Host", "").split(":")[0]
        page = PAGES / f"{host}.html"
        if self.path == "/favicon.ico" or not page.is_file():
            self.send_error(404)
            return
        body = page.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


port, cert, key = int(sys.argv[1]), sys.argv[2], sys.argv[3]
server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.load_cert_chain(cert, key)
server.socket = context.wrap_socket(server.socket, server_side=True)
server.serve_forever()
