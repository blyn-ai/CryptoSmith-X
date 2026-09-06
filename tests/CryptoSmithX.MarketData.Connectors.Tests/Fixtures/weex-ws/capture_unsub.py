import asyncio, json, os, sys, time
from datetime import datetime, timezone
import websockets

URL = "wss://ws-contract.weex.com/v3/ws/public"
OUT = sys.argv[1]
def now(): return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ")

async def drain(ws, secs, out=None, tag=""):
    end, n = time.time() + secs, 0
    while time.time() < end:
        try:
            m = await asyncio.wait_for(ws.recv(), timeout=max(0.05, end - time.time()))
        except asyncio.TimeoutError:
            break
        except websockets.ConnectionClosed as e:
            if out is not None:
                out.append(f"{now()} << CONNECTION CLOSED BY SERVER: code={e.rcvd.code} reason={e.rcvd.reason!r}")
            return -1
        n += 1
        o = json.loads(m)
        if out is not None and o.get("e") is None:
            out.append(f"{now()} < {m}")
    return n

async def unsub():
    lines = ["# WEEX contract V3 public WS - the unsubscribe form.",
             "# '>' is client to server, '<' is server to client. Exact frame text.", ""]
    async with websockets.connect(URL, open_timeout=20, ping_interval=None) as ws:
        await drain(ws, 2)
        req = '{"method":"SUBSCRIBE","params":["btcusdt@depth"],"id":1}'
        lines.append("> " + req); await ws.send(req)
        n = await drain(ws, 10, lines)
        lines.append(f"# {n} frames arrived in the 10 s after subscribing")
        req = '{"method":"UNSUBSCRIBE","params":["btcusdt@depth"],"id":1002}'
        lines.append(""); lines.append("> " + req); await ws.send(req)
        n = await drain(ws, 10, lines)
        lines.append(f"# {n} frames arrived in the 10 s after the unsubscribe ack "
                     f"(the ack itself is counted; the depth stream stops)")
    with open(os.path.join(OUT, "unsubscribe.txt"), "w") as f:
        f.write("\n".join(lines) + "\n")
    print("\n".join(lines))

async def bad_channel():
    """How many rejected subscribes the server tolerates before it kills the socket.
    Load-bearing: a feed that retries an unknown channel loses every other subscription."""
    lines = ["# WEEX contract V3 public WS - what repeated rejected channels do to the socket.",
             "# One invalid subscribe is answered and survivable. Repeats are not.", ""]
    async with websockets.connect(URL, open_timeout=20, ping_interval=None) as ws:
        await drain(ws, 2)
        for i in range(1, 12):
            req = json.dumps({"method": "SUBSCRIBE", "params": [f"btcusdt@bogus_{i}"], "id": i},
                             separators=(",", ":"))
            lines.append(f"{now()} > " + req)
            try:
                await ws.send(req)
            except websockets.ConnectionClosed as e:
                lines.append(f"{now()} << send failed, socket already closed: "
                             f"code={e.rcvd.code if e.rcvd else '?'}")
                break
            if await drain(ws, 2, lines) == -1:
                lines.append(f"# server closed after {i} rejected subscribes on this connection")
                break
    with open(os.path.join(OUT, "invalid-channel-close.txt"), "w") as f:
        f.write("\n".join(lines) + "\n")
    print("\n".join(lines))

asyncio.run(unsub())
asyncio.run(bad_channel())
