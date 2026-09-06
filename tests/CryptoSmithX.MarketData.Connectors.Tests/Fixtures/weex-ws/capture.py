"""Capture the WEEX contract V3 public WS protocol to fixture files.

Every byte written here is the exact text of a frame that crossed the wire; nothing is
reformatted or hand-written. Control frames (PING/PONG/CLOSE) are taken from the websockets
library's own frame log, which is the only place a library-answered protocol ping is visible.
"""
import asyncio, json, logging, os, sys, time
from datetime import datetime, timezone
import websockets

URL = "wss://ws-contract.weex.com/v3/ws/public"
OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)

transcript = []          # annotated, human-readable, in order
control = []             # protocol-level control frames seen in the library log


class FrameLog(logging.Handler):
    def emit(self, record):
        m = record.getMessage()
        if any(k in m for k in ("PING", "PONG", "CLOSE")):
            control.append(f"{now()} {m}")


def now():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")


logging.getLogger("websockets.client").setLevel(logging.DEBUG)
logging.getLogger("websockets.client").addHandler(FrameLog())


def w(name, text):
    with open(os.path.join(OUT, name), "w") as f:
        f.write(text if text.endswith("\n") else text + "\n")
    print(f"wrote {name} ({len(text)} bytes)")


async def main():
    async with websockets.connect(URL, open_timeout=20, ping_interval=None) as ws:
        transcript.append(f"{now()} == HTTP 101, connected to {URL}")
        captured = {}
        deltas = []
        collecting_deltas = False

        async def send(obj_or_raw, note=""):
            raw = obj_or_raw if isinstance(obj_or_raw, str) else json.dumps(obj_or_raw, separators=(",", ":"))
            transcript.append(f"{now()} > {raw}" + (f"        # {note}" if note else ""))
            print(">", raw, flush=True)
            await ws.send(raw)

        async def pump(secs, want=None, limit=None):
            """Read for `secs`; stop early once every key in `want` has been captured."""
            nonlocal collecting_deltas
            end = time.time() + secs
            got = set()
            while time.time() < end:
                try:
                    m = await asyncio.wait_for(ws.recv(), timeout=max(0.05, end - time.time()))
                except asyncio.TimeoutError:
                    break
                transcript.append(f"{now()} < {m}")
                try:
                    o = json.loads(m)
                except Exception:
                    continue
                e = o.get("e")
                if collecting_deltas and e == "depth" and o.get("s") == "BTCUSDT":
                    deltas.append(m)
                    if limit and len(deltas) >= limit:
                        return got
                for key, pred in CAPTURE.items():
                    if key not in captured and pred(o):
                        captured[key] = m
                        got.add(key)
                if want and want <= got:
                    return got
            return got

        CAPTURE = {
            "greeting":   lambda o: o.get("event") == "connected",
            "pong":       lambda o: o.get("id") == 7 and o.get("result") is True,
            "sub_ack":    lambda o: o.get("id") == 1 and o.get("result") is True,
            "sub_error":  lambda o: o.get("result") is False,
            "unsub_ack":  lambda o: o.get("id") == 1002 and o.get("result") is True,
            "snapshot":   lambda o: o.get("e") == "depthSnapshot" and o.get("s") == "BTCUSDT",
            "ticker":     lambda o: o.get("e") == "ticker",
            "kline":      lambda o: o.get("e") == "kline",
            "kline_snap": lambda o: o.get("e") == "klineSnapshot",
        }

        # 1. greeting
        await pump(3, want={"greeting"})

        # 2. application-level ping, client -> server
        await send({"method": "PING", "id": 7}, "application-level ping")
        await pump(3, want={"pong"})

        # 3. depth: ack, snapshot, then a long run of deltas for one symbol
        await send({"method": "SUBSCRIBE", "params": ["btcusdt@depth"], "id": 1}, "default depth level")
        await pump(5, want={"sub_ack", "snapshot"})
        collecting_deltas = True
        await pump(60, limit=60)
        collecting_deltas = False

        # 4. ticker and kline
        await send({"method": "SUBSCRIBE", "params": ["btcusdt@ticker"], "id": 2})
        await pump(8, want={"ticker"})
        await send({"method": "SUBSCRIBE", "params": ["btcusdt@kline_1m"], "id": 3})
        await pump(70, want={"kline", "kline_snap"})

        # 5. unsubscribe
        await send({"method": "UNSUBSCRIBE", "params": ["btcusdt@ticker"], "id": 1002})
        await pump(5, want={"unsub_ack"})

        # 6. exactly ONE rejected channel. The server closes the socket with 1007
        #    "Unrecognized message sent multiple times" after a handful of bad frames,
        #    so this is deliberately the last thing sent and is sent only once.
        await send({"method": "SUBSCRIBE", "params": ["btcusdt@totally_bogus_channel"], "id": 99},
                   "deliberately invalid channel")
        await pump(5, want={"sub_error"})

        missing = [k for k in CAPTURE if k not in captured]
        print("MISSING:", missing, "deltas:", len(deltas), flush=True)

        w("connect-greeting.json", captured["greeting"])
        w("subscribe-ack.json", captured["sub_ack"])
        w("subscribe-error.json", captured["sub_error"])
        w("unsubscribe-ack.json", captured["unsub_ack"])
        w("depth-snapshot.json", captured["snapshot"])
        w("ticker.json", captured["ticker"])
        w("kline.json", captured["kline"])
        w("kline-snapshot.json", captured["kline_snap"])
        w("depth-deltas.jsonl", "\n".join(deltas))
        w("ping-pong.txt",
          "# client -> server, then the server's reply. Exact frame text.\n"
          '> {"method":"PING","id":7}\n'
          "< " + captured["pong"] + "\n"
          "# protocol-level (RFC 6455 opcode 0x9/0xA) control frames seen on this connection:\n"
          + ("\n".join(control) if control else "# none observed during the capture window"))
        w("session-transcript.txt", "\n".join(transcript))


asyncio.run(main())
