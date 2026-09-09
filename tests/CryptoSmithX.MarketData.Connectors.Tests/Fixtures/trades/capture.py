import asyncio, json, sys, os
import websockets

OUT = sys.argv[1]
SECONDS = int(sys.argv[2]) if len(sys.argv) > 2 else 25

async def grab(name, url, subs, secs=SECONDS, limit=40):
    got = []
    try:
        async with websockets.connect(url, ping_interval=None, open_timeout=15) as ws:
            for s in subs:
                await ws.send(json.dumps(s))
            deadline = asyncio.get_event_loop().time() + secs
            while asyncio.get_event_loop().time() < deadline and len(got) < limit:
                try:
                    msg = await asyncio.wait_for(ws.recv(), timeout=3)
                except asyncio.TimeoutError:
                    continue
                got.append(msg)
    except Exception as e:
        got.append(json.dumps({"__capture_error__": repr(e)}))
    path = os.path.join(OUT, f"{name}.jsonl")
    with open(path, "w") as f:
        f.write("\n".join(got) + "\n")
    print(f"== {name}: {len(got)} frames -> {path}")
    for m in got[:6]:
        print("   ", m[:400])

async def main():
    await asyncio.gather(
        grab("binance_aggtrade_force",
             "wss://fstream.binance.com/market/stream",
             [{"method": "SUBSCRIBE", "params": ["btcusdt@aggTrade", "ethusdt@aggTrade", "!forceOrder@arr"], "id": 1}]),
        grab("weex_trade",
             "wss://ws-contract.weex.com/v3/ws/public",
             [{"method": "SUBSCRIBE", "params": ["BTCUSDT@trade", "ETHUSDT@trade"], "id": 1}]),
        grab("kraken_trade",
             "wss://futures.kraken.com/ws/v1",
             [{"event": "subscribe", "feed": "trade", "product_ids": ["PF_XBTUSD", "PF_ETHUSD"]}]),
        grab("hl_trades",
             "wss://api.hyperliquid.xyz/ws",
             [{"method": "subscribe", "subscription": {"type": "trades", "coin": "BTC"}},
              {"method": "subscribe", "subscription": {"type": "trades", "coin": "ETH"}}]),
    )

asyncio.run(main())
