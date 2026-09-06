"""Captures the Binance USDS-M public WebSocket exactly as the feed will speak it.

Everything written by this script is verbatim wire text. Nothing is hand-written from
documentation: the point of this directory is that the last WEEX conclusion which lived
only in prose went stale in silence.
"""
import asyncio, json, time, urllib.request, sys, os
import websockets

OUT = sys.argv[1]
PUBLIC = "wss://fstream.binance.com/public/stream"
MARKET = "wss://fstream.binance.com/market/stream"
REST = "https://fapi.binance.com/fapi/v1/depth?symbol=BTCUSDT&limit=1000"
log = []

def say(s):
    print(s); log.append(s)

async def capture_depth_run():
    """Subscribe one symbol's diff stream, buffer frames, take the REST snapshot mid-run,
    keep buffering. The fixture must SPAN the snapshot or the first-event rule cannot be
    exercised against real bytes."""
    frames = []
    snapshot = None
    async with websockets.connect(PUBLIC, open_timeout=15, ping_interval=None, max_size=16*1024*1024) as ws:
        await ws.send(json.dumps({"method":"SUBSCRIBE","params":["btcusdt@depth@100ms"],"id":1}))
        ack = await asyncio.wait_for(ws.recv(), timeout=10)
        say(f"SUBSCRIBE btcusdt@depth@100ms -> {ack}")
        open(os.path.join(OUT,"subscribe-ack.json"),"w").write(ack+"\n")

        t_end = time.time() + 6
        while time.time() < t_end:
            frames.append(json.loads(await asyncio.wait_for(ws.recv(), timeout=10)))
        say(f"buffered {len(frames)} frames before the REST snapshot")

        snapshot = json.load(urllib.request.urlopen(REST, timeout=20))
        say(f"REST /depth?symbol=BTCUSDT&limit=1000 -> lastUpdateId={snapshot['lastUpdateId']} "
            f"bids={len(snapshot['bids'])} asks={len(snapshot['asks'])}")

        t_end = time.time() + 8
        while time.time() < t_end:
            frames.append(json.loads(await asyncio.wait_for(ws.recv(), timeout=10)))

    data = [f["data"] for f in frames]
    say(f"captured {len(data)} depthUpdate frames, u range {data[0]['u']}..{data[-1]['u']}")
    # trim to a run that straddles the snapshot, small enough to read by eye
    lui = snapshot["lastUpdateId"]
    idx = next((i for i,d in enumerate(data) if d["u"] >= lui), None)
    say(f"first frame with u >= lastUpdateId is index {idx} of {len(data)}")
    lo = max(0, idx-8); hi = min(len(data), idx+52)
    run = data[lo:hi]
    with open(os.path.join(OUT,"depth-deltas.jsonl"),"w") as f:
        for d in run:
            f.write(json.dumps(d, separators=(",",":"))+"\n")
    json.dump(snapshot, open(os.path.join(OUT,"depth-snapshot.json"),"w"))
    say(f"wrote depth-deltas.jsonl with {len(run)} frames (indices {lo}..{hi-1}); "
        f"{sum(1 for d in run if d['u'] < lui)} of them predate the snapshot")
    # the chain rule, checked here on the raw bytes as well as in the C# tests
    breaks = sum(1 for a,b in zip(run, run[1:]) if b["pu"] != a["u"])
    say(f"pu-chain violations across the captured run: {breaks}")

async def capture_bookticker():
    async with websockets.connect(PUBLIC, open_timeout=15, ping_interval=None) as ws:
        await ws.send(json.dumps({"method":"SUBSCRIBE","params":["btcusdt@bookTicker"],"id":7}))
        await asyncio.wait_for(ws.recv(), timeout=10)
        m = await asyncio.wait_for(ws.recv(), timeout=10)
        open(os.path.join(OUT,"book-ticker.json"),"w").write(m+"\n")
        say(f"bookTicker frame: {m.strip()}")

async def silence(name, url, stream, seconds=12):
    t0=time.time()
    async with websockets.connect(url, open_timeout=15, ping_interval=None) as ws:
        say(f"{name}: handshake to {url} SUCCEEDED (HTTP 101) after {time.time()-t0:.2f}s")
        await ws.send(json.dumps({"method":"SUBSCRIBE","params":[stream],"id":99}))
        got=[]
        end=time.time()+seconds
        while time.time()<end:
            try:
                got.append(await asyncio.wait_for(ws.recv(), timeout=max(0.2,end-time.time())))
            except asyncio.TimeoutError:
                break
        acks=[g for g in got if '"result"' in g]
        data=[g for g in got if '"result"' not in g]
        say(f"{name}: subscribed {stream}; acks={acks}; data frames in {seconds}s = {len(data)}")
        return len(data)

async def main():
    await capture_depth_run()
    await capture_bookticker()
    say("")
    say("--- routing: the handshake never tells you the stream is on the wrong path ---")
    a = await silence("public/stream + btcusdt@depth@100ms  (correct)", PUBLIC, "btcusdt@depth@100ms", 8)
    b = await silence("public/stream + !markPrice@arr@1s    (MISROUTED)", PUBLIC, "!markPrice@arr@1s", 12)
    c = await silence("market/stream + btcusdt@depth@100ms  (MISROUTED)", MARKET, "btcusdt@depth@100ms", 12)
    d = await silence("market/stream + !markPrice@arr@1s    (correct)", MARKET, "!markPrice@arr@1s", 8)
    say("")
    say(f"summary: correct paths delivered {a} and {d} frames; misrouted paths delivered {b} and {c}.")
    open(os.path.join(OUT,"session-transcript.txt"),"w").write(
        "\n".join(log)+"\n")

asyncio.run(main())
