# Sweep & Sell for BTCPay Server

Sweep your store's wallets to your exchange and sell what comes in, automatically.

> Early development. In place: the per-store settings page, encrypted exchange keys, the Kraken
> connector, sweeping to the exchange and conversion at the exchange. Hedging comes next.

## Features

- **Sweep to the exchange:** per store and coin, send the hot wallet balance to your exchange's deposit
  address as soon as it reaches a minimum you set. New transactions and blocks trigger a check straight
  away; a regular check (hourly by default) catches anything missed. Optionally send a percentage to
  your cold wallet: a fixed address, or an extended public key that gives a new address for every sweep.
  Confirmed coins only unless you opt in, an optional daily cap per coin, and the exchange's minimum
  deposit is respected. Once the exchange credits the deposit, conversion runs straight away. Starts in
  dry run, where each sweep is signed but not broadcast. Only hot wallets (key on the server) can be swept.
- **Convert at the exchange:** on a schedule, sell chosen coins (e.g. DOGE, LTC) into a target asset
  (e.g. BTC) as soon as the balance clears the market's minimum order. Orders sell immediately at a
  price floor (by default 2% below the 24-hour average) or better and cancel whatever can't fill, so a
  thin market can't take your coins cheaply. Optional daily cap. Starts in dry run, where each order
  is checked with the exchange but not placed. Every run is listed under Recent activity.

## Planned features

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
