# Threat model

Sweep & Sell runs inside BTCPay Server. Per store, it can (1) sweep on-chain hot wallets to an exchange
deposit address and other configured destinations, (2) sell on the exchange when invoices are paid,
(3) convert exchange balances into a target asset, and (4) when the owner turns payout on, withdraw one
asset from the exchange to a withdrawal destination already saved in the exchange account. This model
covers the plugin, not BTCPay itself.

## Assets

| Asset | Where it lives | Impact if compromised |
| --- | --- | --- |
| Exchange API key and secret (trading) | Store setting, encrypted with BTCPay's data-protection key ring | Trading on the account; withdrawal if the user granted it against guidance (the key test flags it) |
| Payout key (withdraw-only, optional) | Its own store setting, encrypted the same way; refused if it can trade or manage withdrawal addresses | Withdrawals to destinations already saved and verified in the exchange account, i.e. the owner's own accounts |
| Payout destination name | Payout settings | Payouts to another destination saved in the same exchange account |
| Hot wallet funds | BTCPay's store wallets. To sweep, the plugin reads the hot wallet's account key from NBXplorer and signs in memory, as BTCPay's own automated payout processor does | Direct loss |
| Sweep destinations (addresses, xpubs) | Store settings | Funds sent to an attacker |
| Exchange balances | The user's exchange account | Bad trades, loss through slippage |
| Activity log | Plugin storage | Leaks business volumes |

## Trust boundaries

- **Browser to BTCPay:** store owners change settings through BTCPay's authenticated UI. Only users with
  `CanModifyStoreSettings` on the store reach the plugin's pages; sweep settings also need the store's wallet
  signing and broadcasting permissions.
- **BTCPay to the exchange:** HTTPS to the exchange's public API, authenticated per the exchange's
  scheme. Exchange responses are untrusted input.
- **BTCPay events to the plugin:** invoice, payment and wallet events come from inside the BTCPay process
  (wallet events from BTCPay's NBXplorer connection), not from webhooks. There is no inbound endpoint that
  can trigger a sweep or sale. A wallet event only says which coin had activity: the sweep reads amounts
  from the wallet itself, so an event can make a sweep happen sooner but never changes what it sends.

## Assumptions

- **One BTCPay Server process per database.** BTCPay Server is deployed as a single process, and the
  plugin's per-store job locks live in that process. Two processes sharing one database could run a
  store's jobs twice; that setup is not supported.

## Threats and mitigations

| Threat | Mitigation |
| --- | --- |
| Database dump or backup reveals exchange keys | Keys encrypted with the data-protection key ring, which is stored apart from the database |
| Keys leak through logs, pages, API or errors | Keys are write-only in the UI; `ExchangeCredentials.ToString()` hides them; never logged; stored in store settings, which BTCPay's API does not return |
| Stolen trading key used to withdraw | The trading key needs no withdrawal permission; the setup guide says not to grant it, and its key test fails if it can withdraw. `IExchangeConnector` has no withdrawal call |
| Stolen trading key used to change where a payout destination's name sends funds | Its key test fails if Kraken's record of the key lists Add or Update Withdrawal Addresses, and every payout run reads the trading key's permissions too (when it is on the payout's exchange) and withdraws nothing while it has either. A trading key saved during a run is checked again under the settings lock before anything is sent |
| Stolen payout key used to add a destination and withdraw there | The exchange's own record of the key's permissions is read on every payout run and by the key test: a key with any permission beyond querying and withdrawing funds (adding or updating withdrawal addresses, placing, cancelling or closing orders, and anything else) is refused, so a stolen payout key can only send to destinations the owner already saved. Payout also reads every page of recent withdrawals before counting what is still in progress |
| Stolen payout key used to trade, or one key that can do both | Payout uses a separate key that needs only Query Funds and Withdraw Funds; its key test fails if it can place orders; neither settings page saves a key the other already holds; and payout refuses to run with the trading key |
| Payout sends funds to an attacker | Withdrawals only name a destination already saved in the exchange account; the connector has no call to add, change or choose an address, and on the exchange (Kraken) only a key with an address permission can add or change destinations by API, which payout refuses. A destination the exchange lists as unverified is refused. Payout settings need `CanModifyStoreSettings`, and changes of exchange, key, asset or destination are written to the payout activity in the same step as the save, before the settings, so a change never takes effect without its record |
| Payout drains the exchange account or pays twice | Opt-in, starts in dry run (a quote from the exchange, nothing sent), required daily cap (fees included), minimum, an amount always kept, a highest fee (also sent to the exchange as its max fee), one withdrawal per run and the store's pause switch. Each withdrawal is saved as pending before it is sent; the cap, the balance and the withdrawals in progress are read again under the settings lock right before sending (so a convert sell meanwhile can't eat into the amount kept, and a run that crosses midnight counts on the new day); a lost response is matched in the exchange's recent withdrawals by amount, time and destination (never to one already accounted for, and only once the match window has passed, so a later withdrawal of the same amount listed first isn't taken for it) and is never retried blindly: unmatched, it waits for the owner to check and remove it, which counts it in full against the day's cap. A withdrawal the exchange accepted stays pending, counted in full, until the exchange lists its reference (an account that needs withdrawals approved lists them only once approved), so nothing more is sent while one may still be waiting for approval. Withdrawals still in progress at the exchange are left out of what can be sent, so one whose balance hasn't left yet is never sent again; each is remembered until the exchange lists it as final, however long it stays on hold. Two stores never pay out live with the same payout key, since neither would see the other's unlisted withdrawals (two different keys for one account can't be detected; the setup guide says one account per store) |
| Attacker changes a sweep destination | Settings need `CanModifyStoreSettings`, and any change to sweeping (or to the exchange or key while sweeping is on, or lifting the pause while sweeping is on), Sweep now, removing a pending sweep and resuming a stopped coin also need BTCPay's permissions to sign and broadcast wallet transactions in the store, so a store manager without wallet rights can't redirect sweeps; cold wallet changes are written to the sweep activity and logged as warnings; exchange deposit addresses are fetched from the exchange at send time, never typed in; a settings save during a run is checked under the store's settings lock before the broadcast, so a sweep never goes out under replaced settings |
| Hot wallet key leaks | The key is only read to sign, kept in memory, never logged, stored or put in a message; wallets without a key on the server are reported, never swept |
| Forged or replayed trigger causes a sale | No external trigger exists: hedges start from BTCPay's internal invoice events, and the invoice is read again from the database before anything is decided. Each invoice is recorded under its ID the first time it is taken, with the BTC still to sell, and never taken again; records are kept longer than the oldest invoice that can be hedged (30 days), and invoices created before hedging was turned on are never hedged |
| Hedge sells BTC the store doesn't have | Hedges sell only from the BTC balance available on the exchange, never on margin or credit; anything more waits until BTC arrives (e.g. a sweep). Convert can't be set to sell BTC while hedging is on, so it can't sell the BTC kept for hedges |
| Restart or retry sends or sells twice | Convert saves each live order as pending (with its client order ID) before sending it, and a store runs one job at a time. Each sweep is signed, then saved as pending with its raw transaction before it is broadcast; until the wallet or node knows it, nothing more of that coin is swept and only that same transaction is rebroadcast, which can be mined only once. A sweep the node refuses on its first broadcast was never sent and is dropped; one refused on rebroadcast may still be on the network, so it stays pending until the owner checks it and removes it (it then counts against the daily cap). Stores sharing a wallet take a wallet lock, so they never build from the same coins at once. Hedge orders are tracked like convert's (below) |
| Lost order response followed by a blind retry sells twice | Orders carry a client order ID derived from the business fact; `FindOrderAsync` looks up open and closed orders by it. Convert sells nothing more while any pending order is unresolved, and only treats an order as never placed when the exchange still has no record of it after a grace period; live orders carry the exchange's order deadline (30 seconds on Kraken), well inside that period, so a delayed request can't execute after it. Each pending order remembers the exchange and key fingerprint it was sent with: it is never looked up on another account, and while orders are pending only that exchange and key can be saved (checked under the store's job lock, so a run can't send an order in between; removing the key always can, without waiting). An order is dropped from pending only when the exchange shows it closed (a fill with no amount is not counted as a sale), definitively refused it, or still has no record of it after the grace period (an order the exchange confirmed with an order ID is never treated as unplaced, and one still open after placing stays pending, so its final fill is counted before anything more sells); the owner can remove a pending order by hand after checking the exchange. Hedge uses the same lookup (`PendingOrders`); while orders of either job are pending, only their key can be saved. A hedge order removed by hand counts as fully sold, so its invoices are never hedged twice |
| Malicious or malformed exchange response (huge amount, wrong address) | Sweep amounts come from the store's own wallet, never from the exchange; the exchange's deposit address is validated for the coin's network on this server before anything is signed. Only deposit addresses that never expire are used, since a sweep may confirm after an expiry. Sell amounts are checked against the exchange balances and daily caps; hedge amounts come from the invoice's own payments (or price and rate), never from the exchange |
| Deposit sent without a required tag or memo is not credited | Connectors return the tag with the address; sweeps refuse destinations that need one |
| Deposit below the exchange minimum is lost | Minimum deposits read from the exchange; a sweep waits until the exchange's share, after the network fee and cold wallet share, clears it |
| Deposit never credited, or credited without being sold | Each sent sweep is watched on the exchange (by transaction ID, on the exchange it was sent to, searched from when it was signed) for 24 hours, and never dropped on a failed lookup; an uncredited deposit is reported to the owner and stops its coin, and a credit of nothing is treated as an error. A credited deposit starts a convert run straight away, and the scheduled convert still sells anything that arrives later |
| Returned deposit swept back again and again, paying fees each time | A deposit the exchange fails or returns, or doesn't credit within the watch, stops sweeping of that coin until the owner resumes it (which needs wallet permissions); a pending sweep removed by hand is watched too, in case it was sent |
| Flood of wallet events (e.g. a block confirming many transactions) | Events are coalesced per coin, and a store runs one sweep at a time |
| Sale fills far below value on a thin or manipulated market | No unpriced market orders: sells are immediate-or-cancel limits at a floor set a configurable percentage (default 2%) below the exchange's 24-hour average price, which a single trade can't drag down; when the best bid is below the floor, nothing is sent. Optional daily cap on value sold, kept per target asset and counted pessimistically when a fill is unknown (an order removed by hand counts in full). Dry run to observe before going live. The same floor and a daily cap in BTC apply to hedges; invoices taken in dry run are never sold later |
| One store reads another's keys | Settings are keyed by store ID and only loaded for the authorized store. When stores share a wallet, a store waiting for another's unsettled sweep is told only that one exists; the other store is named in the server log, not in its activity |
| Data-protection keys lost after a migration | Decryption failure reads as "no key saved", and the owner re-enters it; nothing crashes or leaks |

## Out of scope

Compromise of the BTCPay server host or its admin accounts, and of the exchange itself. With host access an
attacker can already spend hot wallet funds.
