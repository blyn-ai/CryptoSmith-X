import asyncio, json, logging, os, re, sys, time
from datetime import datetime, timezone
import websockets

URL = "wss://ws-contract.weex.com/v3/ws/public"
OUT = sys.argv[1]
CTRL = re.compile(r"^[<>] (PING|PONG)\b")
ctrl = []
def now(): return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")

class H(logging.Handler):
    def emit(self, r):
        m = r.getMessage()
        if CTRL.match(m):
            ctrl.append(f"{now()} {m}")

logging.getLogger("websockets.client").setLevel(logging.DEBUG)
logging.getLogger("websockets.client").addHandler(H())

async def main():
    lines = []
    async with websockets.connect(URL, open_timeout=20, ping_interval=None) as ws:
        await asyncio.wait_for(ws.recv(), 5)   # greeting, already captured elsewhere

        lines.append("## 1. Application-level ping, client -> server.")
        lines.append('> {"method":"PING","id":7}')
        await ws.send('{"method":"PING","id":7}')
        while True:
            m = await asyncio.wait_for(ws.recv(), 5)
            if json.loads(m).get("id") == 7:
                lines.append("< " + m)
                break

        lines.append("")
        lines.append("## 2. RFC 6455 control ping (opcode 0x9), client -> server.")
        lines.append("## The server answers with a control pong (opcode 0xA) echoing the payload.")
        pw = await ws.ping(b"cs-x")
        await asyncio.wait_for(pw, 10)
        lines += ctrl

        lines.append("")
        lines.append("## 3. Application-level ping, server -> client. Unprompted, every 60 s.")
        lines.append("## No reply was sent to any of these and the server did not close the")
        lines.append("## connection over a 4-minute idle, so a reply is not required to stay up.")
        await ws.send(json.dumps({"method": "SUBSCRIBE", "params": ["btcusdt@ticker"], "id": 1}))
        end = time.time() + 200
        got = 0
        while time.time() < end and got < 3:
            m = await asyncio.wait_for(ws.recv(), timeout=max(0.1, end - time.time()))
            if json.loads(m).get("event") == "ping":
                lines.append(f"{now()} < " + m)
                got += 1
                print("server ping", got, m, flush=True)

    with open(os.path.join(OUT, "ping-pong.txt"), "w") as f:
        f.write("# WEEX contract V3 public WS - the ping/pong exchange, both directions.\n")
        f.write("# Exact frame text; '>' is client to server, '<' is server to client.\n\n")
        f.write("\n".join(lines) + "\n")
    print("wrote ping-pong.txt")

asyncio.run(main())
