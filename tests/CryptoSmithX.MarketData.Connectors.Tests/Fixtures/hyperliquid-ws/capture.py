import asyncio, json, sys, os
import websockets

URL = "wss://api.hyperliquid.xyz/ws"
OUT = sys.argv[1] if len(sys.argv) > 1 else "."

async def main():
    frames = {"activeAssetCtx": [], "candle": [], "l2Book": [], "other": []}
    async with websockets.connect(URL, ping_interval=None) as ws:
        for sub in [
            {"method": "subscribe", "subscription": {"type": "activeAssetCtx", "coin": "BTC"}},
            {"method": "subscribe", "subscription": {"type": "candle", "coin": "BTC", "interval": "1m"}},
        ]:
            await ws.send(json.dumps(sub))
            print("> " + json.dumps(sub))

        deadline = asyncio.get_event_loop().time() + 15
        while asyncio.get_event_loop().time() < deadline:
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=2)
            except asyncio.TimeoutError:
                continue
            try:
                d = json.loads(msg)
            except Exception:
                continue
            ch = d.get("channel", "other")
            key = ch if ch in frames else "other"
            frames[key].append(msg)
            print("<", msg[:200])

    for name, lst in frames.items():
        if lst:
            with open(os.path.join(OUT, f"{name}.jsonl"), "w") as f:
                f.write("\n".join(lst) + "\n")
            print(f"saved {len(lst)} frames -> {name}.jsonl")

asyncio.run(main())
