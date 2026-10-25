# Changelog

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
