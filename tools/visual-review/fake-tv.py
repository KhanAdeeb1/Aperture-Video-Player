"""A stand-in DLNA TV for the visual review: answers SSDP searches and UPnP AV actions.

It behaves like a simple TV: it fetches the media it is given (the start of it, to check it is
there), then "plays" in real time. Nothing is decoded or shown. Standard library only.

    python fake-tv.py --name "Living Room TV" --port 49500 --log fake-tv.log
"""

import argparse
import re
import socket
import struct
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from xml.sax.saxutils import escape

GROUP = ("239.255.255.250", 1900)
UDN = "uuid:5e1f7a00-0000-4000-8000-00000000c1a5"


class Tv:
    def __init__(self, log):
        self.lock = threading.Lock()
        self.state = "NO_MEDIA_PRESENT"
        self.status = "OK"
        self.uri = ""
        self.base = 0.0
        self.since = None
        self.log_file = log

    def log(self, text):
        line = time.strftime("%H:%M:%S ") + text
        print(line, flush=True)
        with open(self.log_file, "a", encoding="utf-8") as f:
            f.write(line + "\n")

    def position(self):
        with self.lock:
            return self.base + (time.monotonic() - self.since if self.since else 0)

    def load(self, uri):
        try:
            request = urllib.request.Request(uri, headers={"Range": "bytes=0-65535"})
            with urllib.request.urlopen(request, timeout=20) as response:
                data = response.read(65536)
                self.log(f"fetched {response.status} {response.headers.get('Content-Type')} {len(data)} bytes, "
                         f"contentFeatures={response.headers.get('contentFeatures.dlna.org')}")
                ok = len(data) > 0
        except Exception as error:  # the TV just fails to play, as a real one would
            self.log(f"fetch failed: {error}")
            ok = False
        with self.lock:
            if self.uri != uri:
                return
            if ok:
                self.state, self.base, self.since = "PLAYING", 0.0, time.monotonic()
            else:
                self.state, self.status = "STOPPED", "ERROR_OCCURRED"


def fmt(seconds):
    s = int(max(0, seconds))
    return f"{s // 3600}:{s // 60 % 60:02}:{s % 60:02}"


def parse(text):
    m = re.fullmatch(r"(\d+):(\d\d):(\d\d)(\.\d+)?", text.strip())
    return int(m.group(1)) * 3600 + int(m.group(2)) * 60 + int(m.group(3)) if m else 0


def handler_for(tv, name):
    description = f"""<?xml version="1.0"?>
<root xmlns="urn:schemas-upnp-org:device-1-0"><specVersion><major>1</major><minor>0</minor></specVersion>
<device><deviceType>urn:schemas-upnp-org:device:MediaRenderer:1</deviceType>
<friendlyName>{escape(name)}</friendlyName><manufacturer>Aperture CI</manufacturer><modelName>Stand-in TV</modelName>
<UDN>{UDN}</UDN><serviceList>
<service><serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType><serviceId>urn:upnp-org:serviceId:AVTransport</serviceId><controlURL>/avt</controlURL></service>
<service><serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType><serviceId>urn:upnp-org:serviceId:ConnectionManager</serviceId><controlURL>/cm</controlURL></service>
</serviceList></device></root>"""

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def reply(self, status, body, type="text/xml; charset=\"utf-8\""):
            data = body.encode()
            self.send_response(status)
            self.send_header("Content-Type", type)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            if self.path == "/description.xml":
                self.reply(200, description)
            else:
                self.reply(404, "")

        def do_POST(self):
            body = self.rfile.read(int(self.headers.get("Content-Length", 0))).decode()
            action = re.search(r"#(\w+)", self.headers.get("SOAPACTION", "")).group(1)

            def arg(n):
                m = re.search(rf"<{n}>(.*?)</{n}>", body, re.S)
                return m.group(1).replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">") if m else ""

            result = ""
            with tv.lock:
                if action == "GetProtocolInfo":
                    result = "<Source></Source><Sink>http-get:*:video/mp4:*,http-get:*:video/x-matroska:*,http-get:*:video/mpeg:*,http-get:*:audio/mpeg:*</Sink>"
                elif action == "SetAVTransportURI":
                    tv.uri, tv.state, tv.status, tv.base, tv.since = arg("CurrentURI"), "STOPPED", "OK", 0.0, None
                elif action == "Play":
                    if tv.state == "PAUSED_PLAYBACK":
                        tv.state, tv.since = "PLAYING", time.monotonic()
                    else:
                        tv.state = "TRANSITIONING"
                        threading.Thread(target=tv.load, args=(tv.uri,), daemon=True).start()
                elif action == "Pause":
                    tv.base += time.monotonic() - tv.since if tv.since else 0
                    tv.since, tv.state = None, "PAUSED_PLAYBACK"
                elif action == "Stop":
                    tv.base, tv.since, tv.state = 0.0, None, "STOPPED"
                elif action == "Seek":
                    tv.base = parse(arg("Target"))
                    tv.since = time.monotonic() if tv.state == "PLAYING" else None
                elif action == "GetTransportInfo":
                    result = f"<CurrentTransportState>{tv.state}</CurrentTransportState><CurrentTransportStatus>{tv.status}</CurrentTransportStatus><CurrentSpeed>1</CurrentSpeed>"
                elif action == "GetPositionInfo":
                    position = tv.base + (time.monotonic() - tv.since if tv.since else 0)
                    result = (f"<Track>1</Track><TrackDuration>0:00:00</TrackDuration><TrackMetaData></TrackMetaData>"
                              f"<TrackURI>{escape(tv.uri)}</TrackURI><RelTime>{fmt(position)}</RelTime><AbsTime>NOT_IMPLEMENTED</AbsTime>")
            if action not in ("GetTransportInfo", "GetPositionInfo"):
                tv.log(f"{action} -> {tv.state}")
            self.reply(200, '<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body>'
                            f'<u:{action}Response xmlns:u="urn:schemas-upnp-org:service:AVTransport:1">{result}</u:{action}Response>'
                            '</s:Body></s:Envelope>')

    return Handler


def local_address_for(remote):
    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        probe.connect((remote, 9))
        return probe.getsockname()[0]
    finally:
        probe.close()


def answer_searches(tv, port):
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind(("", GROUP[1]))
    for address in {a[4][0] for a in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET)} | {"0.0.0.0"}:
        try:
            sock.setsockopt(socket.IPPROTO_IP, socket.IP_ADD_MEMBERSHIP, socket.inet_aton(GROUP[0]) + socket.inet_aton(address))
        except OSError:
            pass
    tv.log("listening for SSDP searches")
    while True:
        data, sender = sock.recvfrom(4096)
        text = data.decode(errors="replace")
        if not text.startswith("M-SEARCH") or not re.search(r"(?im)^ST:\s*(urn:schemas-upnp-org:device:MediaRenderer:1|ssdp:all)\s*$", text):
            continue
        host = local_address_for(sender[0])
        tv.log(f"search from {sender[0]}; answering with {host}")
        answer = ("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nEXT:\r\n"
                  f"LOCATION: http://{host}:{port}/description.xml\r\nSERVER: Windows UPnP/1.0 FakeTV/1.0\r\n"
                  "ST: urn:schemas-upnp-org:device:MediaRenderer:1\r\n"
                  f"USN: {UDN}::urn:schemas-upnp-org:device:MediaRenderer:1\r\n\r\n")
        sock.sendto(answer.encode(), sender)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--name", default="Living Room TV")
    parser.add_argument("--port", type=int, default=49500)
    parser.add_argument("--log", default="fake-tv.log")
    options = parser.parse_args()
    tv = Tv(options.log)
    threading.Thread(target=answer_searches, args=(tv, options.port), daemon=True).start()
    tv.log(f"{options.name} on port {options.port}")
    ThreadingHTTPServer(("0.0.0.0", options.port), handler_for(tv, options.name)).serve_forever()


if __name__ == "__main__":
    main()
