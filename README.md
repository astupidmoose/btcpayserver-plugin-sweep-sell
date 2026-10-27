# Sweep & Sell for BTCPay Server

Sweep your store's wallets to your exchange and sell what comes in, automatically.

> Beta. Every feature is in place and starts in dry run; test it on your own server before letting it
> move money. See [CHANGELOG.md](CHANGELOG.md) for what each release contains.

## Features

- **Sweep to the exchange:** per store and coin, send the hot wallet balance to your exchange's deposit
  address as soon as it reaches a minimum you set. New transactions and blocks trigger a check straight
  away; a regular check (hourly by default) catches anything missed. Optionally send a percentage to
  your cold wallet: a fixed address, or an extended public key that gives a new address for every sweep.
  Confirmed coins only unless you opt in, an optional daily cap per coin, and the exchange's minimum
  deposit is respected. Once the exchange credits the deposit, conversion runs straight away. Starts in
  dry run, where each sweep is signed but not broadcast. Only hot wallets (key on the server) can be swept, and changing sweeps needs permission to sign and
  broadcast wallet transactions in the store.
- **Convert at the exchange:** on a schedule, sell chosen coins (e.g. DOGE, LTC) into a target asset
  (e.g. BTC) as soon as the balance clears the market's minimum order. Orders sell immediately at a
  price floor (by default 2% below the 24-hour average) or better and cancel whatever can't fill, so a
  thin market can't take your coins cheaply. Optional daily cap. Starts in dry run, where each order
  is checked with the exchange but not placed. Every run is listed under Recent activity.
- **Hedge on payment:** when an invoice reaches the status you pick (paid in full or settled), sell the
  BTC it brought in on your exchange straight away, to lock in its price. Each invoice is hedged once,
  into the market you choose per invoice currency (e.g. GBP invoices into CAD). Sells come from BTC
  already on the exchange and never borrow; amounts below the minimum order wait until they add up.
  Same price floor and optional daily cap as convert, and dry run by default. Invoices created before
  hedging was turned on are never hedged.
- **Payout (optional):** on a schedule, withdraw one asset (e.g. the currency you convert to) to a
  withdrawal destination you already saved and verified in your exchange, such as your bank account. It
  uses its own withdraw-only key and can't add or change destinations: you enter the name of one that is
  already there. Required daily cap, minimum, an amount to keep and a highest fee. Starts in dry run,
  where the exchange quotes each withdrawal but nothing is sent. Lives on its own page, linked from the
  settings page.
- **Safety:** dry run for every feature, daily caps, a pause switch per store, and an activity log for
  each feature.

## Exchanges

| Exchange | Status |
| --- | --- |
| Kraken | Beta |

Adding an exchange means implementing `IExchangeConnector` (see `src/.../Exchanges`). Contributions welcome.

## Install

Needs BTCPay Server 2.4.5 or later, and a server admin account.

1. Download `BTCPayServer.Plugins.SweepSell.btcpay` and `SHA256SUMS` from the
   [latest release](../../releases), and check the package with `sha256sum -c SHA256SUMS --ignore-missing`.
2. In BTCPay Server, open **Plugins > Installed Plugins**, click **Upload Plugin**, choose the file
   and click **Upload**.
3. Click **Restart now** when BTCPay asks for a restart.
4. Open a store and click **Sweep & Sell** in the left menu. Add an exchange key (below), then turn on the
   features you want. Each one stays in dry run until you turn dry run off.

To build the package yourself, run `scripts/pack.sh` (it writes to `dist/`).

## Your exchange key

Create a key for this plugin only, with permission to query funds, deposit funds, query open and closed orders and create
orders, and **without withdrawal permission** or permission to add or update withdrawal addresses. The key
is stored encrypted on your BTCPay server and never shown again after you save it. The settings page has a
**Test the key** button, which checks each permission without trading, and that the key can't withdraw or
change withdrawal addresses. Payout won't run while the trading key can change withdrawal addresses.

Payout needs a second key with only **query funds** and **withdraw funds**, and no query start or end
date: payout refuses a key with any other permission, such as an order or withdrawal address permission. **Test the payout key**
checks that it can withdraw, can't trade and has no other permission, and lists the withdrawal
destinations your exchange shows for the asset.

## Several stores

Each store has its own Sweep & Sell settings. To set up more stores the same way, use **Copy to other stores**
on the settings page: it copies the sweep, hedge and convert settings (and, if you tick it, the exchange key)
to the stores you pick, with every feature in dry run there.

Stores may trade on the same exchange account. Each asset of the account can be paid out by one store: for
example one store pays out USD and another pays out CAD from the same account, each to its own saved
destination. Payout won't run while another store pays out the same asset from the same account. No store
sharing that account should sell an asset that is paid out (for example convert it into something else), since
that eats into the amount payout keeps; payout checks this for its own store but can't see other stores'
settings. Selling into the payout asset, such as hedging BTC into the currency you withdraw, is fine.

## Development

```bash
git clone --recurse-submodules <this repo>
dotnet build src/BTCPayServer.Plugins.SweepSell
dotnet test --project tests/BTCPayServer.Plugins.SweepSell.Tests
./plugin-register.sh   # load the plugin when debugging BTCPay Server
```

To release, make sure `CHANGELOG.md` has a section for the `<Version>` in the plugin's csproj, then run
the **Release** workflow on `main` from the Actions tab with the new tag (e.g. `v0.1.0-beta.1`), or push
that tag on a commit on `main`. It tests, packages and publishes the GitHub release.

See [AGENTS.md](AGENTS.md) for the rules every change follows, and [SECURITY.md](SECURITY.md) to
report a vulnerability.
