# CryptoSmith-X

Algorithmic crypto-trading software. A product of MB „BlynAI“ — https://blynai.eu.

| | |
|---|---|
| Site | https://blynai.eu |
| Live journal | https://blynai.meetluko.eu |
| Contact | info@blynai.eu |

CryptoSmith-X is a rewrite of [trading-bot](https://github.com/bykovas/trading-bot).
The system it replaces is a set of .NET services — an API, a web UI, spot, futures
and market-data workers over PostgreSQL — running as one docker compose stack on a
single server.

> **Status:** early. Nothing here trades yet; trading-bot remains the live system
> until the rewrite takes over.

## What this is meant to become

Three products on one foundation — Studio & API (market data and a public gateway),
Agent (an autonomous execution environment) and Arena (a verified strategy
leaderboard) — and an honest account of how much of each exists today:
[docs/product-vision.md](docs/product-vision.md).

## Disclaimer

The Lithuanian text published on https://blynai.eu is the authoritative version.
The English below is a convenience translation for readers of this repository and
carries no separate legal force.

MB „BlynAI“ is not a financial institution, an investment manager or a provider of
financial services. The company gives no investment advice, does not accept or hold
third-party money or crypto-assets, and does not operate third-party exchange
accounts. The company trades only with its own funds; published results are research
data, not an offer or a forecast.

MB „BlynAI“ is a two-member research partnership. Its activity is writing algorithmic
trading software, studying how that software behaves using the company's own funds,
and publishing the results.

### Trading with own funds

The software runs as two independent instances — LUKO (blynai.meetluko.eu) and BYKO
(blynai.bykovas.lt). The same code, separate accounts, separate journals.

- trading is done only with the funds of the company and of its members;
- each instance runs in its own exchange account, with its own API keys;
- the strategy, the risk limits and the parameters are set by the members themselves;
- there are no client funds, and there will be none.

### No third-party assets

The company:

- does not accept or hold third-party money or crypto-assets;
- does not operate third-party exchange accounts;
- neither holds nor requests API permissions that would allow it to dispose of
  another person's funds;
- does not carry out collective investment or pooled capital management;
- does not accept investments and gives no individual investment advice.

### Published results

Published data are the historical research data of one specific instance over one
specific period. They do not guarantee future results and are not an offer, a
forecast or individual investment advice. Failed experiments are published the same
way successful ones are.

### Open source

The software is released as open source so that a third party can reproduce the
result without the company's involvement. It is released under the MIT License
(see [LICENSE](LICENSE)) and provided as is, without warranties.
Anyone deploying it does so on infrastructure they control, in their own exchange
account and at their own risk; the company makes no trading decisions on their behalf
and does not control their account or their funds.

### Company status

MB „BlynAI“ is a mažoji bendrija in the Republic of Lithuania. The company number is
pending registration and will be stated on the site once the company is entered in
the Register of Legal Entities. The members are Lukas Peciukonis and Denisas Bykovas,
50 % each. The manager is Lukas Peciukonis. Contact: info@blynai.eu.
