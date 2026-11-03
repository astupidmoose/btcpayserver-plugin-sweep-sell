# Changelog

## 0.1.5 (beta)

- **Sweeps signal replace-by-fee** (BIP 125), so a sweep stuck below the going fee can be bumped from the wallet.
- **Bumped sweeps are followed:** when a sent sweep the exchange hasn't credited (pending, failed or not seen) was replaced (e.g. by a fee bump), its
  deposit is looked for by the new transaction instead, with a fresh 24-hour watch; a credit for any transaction it
  replaced still counts, in case a miner confirms one anyway, and one the exchange reports failed stops sweeping the coin. The wallet is asked about replacements every 10 minutes per deposit, and
  straight away when the exchange reports a deposit failed or its watch is ending. If the wallet can't say whether a
  sweep was replaced, the activity shows it once and the deposit is never given up on while that check fails.

## 0.1.4 (beta)

- **Kraken rate limits:** waiting sweep deposits are looked up together, one request per coin each minute instead
  of one or more per deposit, and each coin's deposit method is reused for 30 minutes (the minimum deposit is still
  read fresh before each sweep). Calls with the same Kraken key
  now wait their turn under Kraken's lowest rate limit instead of being refused with "EAPI:Rate limit exceeded", which
  also left convert and hedge unable to read balances.
- **Sweep activity:** a deposit check refused for too many requests is one entry instead of one per deposit, and
  stops that minute's checks; a failed check of several deposits of a coin is one entry for the coin.

## 0.1.3 (beta)

- **Sweep fees:** a new store's sweeps aim for confirmation within 3 blocks (about half an hour for Bitcoin, a
  medium priority fee) instead of 6. Stores already set up keep their setting; change "Network fee: confirm within (blocks)" on the
  settings page to use the new target.
- **Sweep activity:** each sweep now shows the fee rate it pays (sat/vB for Bitcoin) and the confirmation target it aimed for,
  so it can be compared with the network's fee estimates.

## 0.1.2 (beta)

- **Payout:** stores can now pay out different assets of the same exchange account, for example USD from one
  store and CAD from another, each to its own saved destination. Before, a payout from one store blocked every
  other store's payout from that account. Two stores still can't pay out the same asset of one account.

## 0.1.1 (beta)

Fixes from testing the first beta. Install it over 0.1.0; settings and activity are kept.

- **Sweep:** a sweep at a fee below 1 sat/vB, which the node accepts, no longer fails with "Fee too low". The
  plugin now uses the node's own minimum relay fee, and never pays less than it.
- **Copy to other stores:** a new section on the settings page copies the sweep, hedge and convert settings
  (and, if you tick it, the exchange key) to other stores you manage. Every copied feature lands in dry run,
  each store keeps its own pause switch and payout settings, and sweep rules are copied only for coins that
  store has a wallet for.
- **Convert:** a run with nothing to sell now says so in the activity log, instead of leaving it empty.
- **Settings page:** says above each feature's activity when it doesn't run because it is off or the store
  is paused, and notes under each dry run switch that dry runs only happen while the feature is on.

## 0.1.0 (beta)

First release, for testing. Every feature starts in dry run and stays there until you turn it off, so
install it, set it up and watch the activity log before letting it move money.

- **Settings:** a Sweep & Sell page for each store, with the exchange key stored encrypted on the server,
  a key test that checks each permission without trading, a pause switch for the store and an activity
  log for each feature.
- **Kraken:** the first exchange. The trading key must not be able to withdraw or change withdrawal
  addresses.
- **Sweep to the exchange:** sends a hot wallet's balance to the exchange's deposit address as soon as it
  passes the minimum you set, with an optional share to a cold wallet (fixed address or extended public
  key), an optional daily cap and the exchange's minimum deposit respected. New transactions and blocks
  trigger a check straight away, and a regular check catches anything missed.
- **Convert at the exchange:** sells chosen coins into a target asset once the balance clears the
  market's minimum order, as immediate-or-cancel orders at a price floor below the 24-hour average.
  Runs on a schedule and as soon as a swept deposit is credited.
- **Hedge on payment:** sells the BTC each paid or settled invoice brought in, once per invoice, from
  BTC already on the exchange, with the same price floor and an optional daily cap.
- **Payout:** withdraws one asset to a destination already saved and verified in your exchange account,
  with its own withdraw-only key, a required daily cap, a minimum, an amount to keep and a highest fee.
  It can't add or change withdrawal destinations, and a withdrawal whose response was lost is looked up
  in the exchange's history rather than sent again.

Needs BTCPay Server 2.4.5 or later. Known limits are listed in `docs/threat-model.md` in the repository.
