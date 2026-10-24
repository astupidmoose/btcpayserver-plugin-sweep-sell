# Sweep & Sell for BTCPay Server

Sweep your store's wallets to your exchange and sell what comes in, automatically.

> Early development. In place: the per-store settings page, encrypted exchange keys, the Kraken
> connector and conversion at the exchange. Sweeping and hedging come next.

## Features

- **Convert at the exchange:** on a schedule, sell chosen coins (e.g. DOGE, LTC) into a target asset
  (e.g. BTC) as soon as the balance clears the market's minimum order. Orders sell immediately at a
  price floor (by default 2% below the 24-hour average) or better and cancel whatever can't fill, so a
  thin market can't take your coins cheaply. Optional daily cap. Starts in dry run, where each order
  is checked with the exchange but not placed. Every run is listed under Recent activity.

## Planned features

- **Sweep rules:** per store and coin, send the hot wallet balance to a fresh deposit address on your
  exchange on a schedule, once it is above a minimum. Optionally split a percentage to other
  destinations: a fixed address or an xpub, which gives a new address each time.
- **Hedge on payment:** when an invoice reaches the status you pick (Processing or Settled), sell the
  BTC it brought in on your exchange straight away.
- **Safety:** dry run for every feature, daily caps, a pause switch per store, and an activity log.

## Exchanges

| Exchange | Status |
| --- | --- |
| Kraken | In progress |

Adding an exchange means implementing `IExchangeConnector` (see `src/.../Exchanges`). Contributions welcome.

## Your exchange key

Create a key for this plugin only, with permission to query funds, deposit funds, query open and closed orders and create
orders, and **without withdrawal permission**. The key is stored encrypted on your BTCPay server and
never shown again after you save it. The settings page has a **Test the key** button, which checks
each permission without trading.

## Development

```bash
git clone --recurse-submodules <this repo>
dotnet build src/BTCPayServer.Plugins.SweepSell
dotnet test --project tests/BTCPayServer.Plugins.SweepSell.Tests
./plugin-register.sh   # load the plugin when debugging BTCPay Server
```

See [AGENTS.md](AGENTS.md) for the rules every change follows, and [SECURITY.md](SECURITY.md) to
report a vulnerability.
