import asyncio, json, sys, os
import websockets

URL = "wss://fstream.binance.com/market/stream"
OUT = sys.argv[1] if len(sys.argv) > 1 else "."

async def main():
    frames = {"ticker_arr": [], "markPrice_arr": [], "kline": [], "ack": [], "other": []}
    async with websockets.connect(URL, ping_interval=None) as ws:
        sub = {"method": "SUBSCRIBE", "params": ["!ticker@arr", "!markPrice@arr@1s", "btcusdt@kline_1m"], "id": 1}
        await ws.send(json.dumps(sub))
        print("> " + json.dumps(sub))

        deadline = asyncio.get_event_loop().time() + 12
        n = 0
        while asyncio.get_event_loop().time() < deadline and n < 300:
            try:
                msg = await asyncio.wait_for(ws.recv(), timeout=2)
            except asyncio.TimeoutError:
                continue
            n += 1
            try:
                d = json.loads(msg)
            except Exception:
                frames["other"].append(msg)
                continue

            if isinstance(d, dict) and "result" in d:
                frames["ack"].append(msg)
                print("ack:", msg[:150])
                continue

            if isinstance(d, list):
                # array push: check first element's e
                et = d[0].get("e") if d and isinstance(d[0], dict) else None
                if et == "24hrTicker":
                    frames["ticker_arr"].append(msg)
                elif et == "markPriceUpdate":
                    frames["markPrice_arr"].append(msg)
                else:
                    frames["other"].append(msg)
                print(f"array[{len(d)}] e={et}")
                continue

            if isinstance(d, dict) and d.get("e") == "kline":
                frames["kline"].append(msg)
                print("kline:", msg[:200])
                continue

            frames["other"].append(msg)
            print("other:", msg[:150])

    for name, lst in frames.items():
        if lst:
            with open(os.path.join(OUT, f"{name}.jsonl"), "w") as f:
                f.write("\n".join(lst) + "\n")
            print(f"saved {len(lst)} -> {name}.jsonl")
        else:
            print(f"NOTHING captured for {name}")

asyncio.run(main())
