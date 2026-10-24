# Threat model

Sweep & Sell runs inside BTCPay Server. Per store, it can (1) sweep on-chain hot wallets to an exchange
deposit address and other configured destinations, (2) sell on the exchange when invoices are paid, and
(3) convert exchange balances into a target asset. This model covers the plugin, not BTCPay itself.

## Assets

| Asset | Where it lives | Impact if compromised |
| --- | --- | --- |
| Exchange API key and secret | Store setting, encrypted with BTCPay's data-protection key ring | Trading on the account; withdrawal if the user granted it against guidance |
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

Mitigations marked *(planned)* arrive with the feature that needs them; the rest are in place.

| Threat | Mitigation |
| --- | --- |
| Database dump or backup reveals exchange keys | Keys encrypted with the data-protection key ring, which is stored apart from the database |
| Keys leak through logs, pages, API or errors | Keys are write-only in the UI; `ExchangeCredentials.ToString()` hides them; never logged; stored in store settings, which BTCPay's API does not return |
| Stolen exchange key used to withdraw | The plugin needs and requests no withdrawal permission; the setup guide says not to grant it |
| Attacker changes a sweep destination | Settings need `CanModifyStoreSettings`, and any change to sweeping (or to the exchange or key while sweeping is on, or lifting the pause while sweeping is on), Sweep now, removing a pending sweep and resuming a stopped coin also need BTCPay's permissions to sign and broadcast wallet transactions in the store, so a store manager without wallet rights can't redirect sweeps; cold wallet changes are written to the sweep activity and logged as warnings; exchange deposit addresses are fetched from the exchange at send time, never typed in; a settings save during a run is checked under the store's settings lock before the broadcast, so a sweep never goes out under replaced settings |
| Hot wallet key leaks | The key is only read to sign, kept in memory, never logged, stored or put in a message; wallets without a key on the server are reported, never swept |
| Forged or replayed trigger causes a sale | No external trigger exists; each sale is recorded under its invoice ID and never repeated *(planned)* |
| Restart or retry sends or sells twice | Convert saves each live order as pending (with its client order ID) before sending it, and a store runs one job at a time. Each sweep is signed, then saved as pending with its raw transaction before it is broadcast; until the wallet or node knows it, nothing more of that coin is swept and only that same transaction is rebroadcast, which can be mined only once. A sweep the node refuses on its first broadcast was never sent and is dropped; one refused on rebroadcast may still be on the network, so it stays pending until the owner checks it and removes it (it then counts against the daily cap). Stores sharing a wallet take a wallet lock, so they never build from the same coins at once. Hedges *(planned)* |
| Lost order response followed by a blind retry sells twice | Orders carry a client order ID derived from the business fact; `FindOrderAsync` looks up open and closed orders by it. Convert sells nothing more while any pending order is unresolved, and only treats an order as never placed when the exchange still has no record of it after a grace period; live orders carry the exchange's order deadline (30 seconds on Kraken), well inside that period, so a delayed request can't execute after it. Each pending order remembers the exchange and key fingerprint it was sent with: it is never looked up on another account, and while orders are pending only that exchange and key can be saved (checked under the store's job lock, so a run can't send an order in between; removing the key always can, without waiting). An order is dropped from pending only when the exchange shows it closed (a fill with no amount is not counted as a sale), definitively refused it, or still has no record of it after the grace period (an order the exchange confirmed with an order ID is never treated as unplaced, and one still open after placing stays pending, so its final fill is counted before anything more sells); the owner can remove a pending order by hand after checking the exchange. Hedge *(planned)* |
| Malicious or malformed exchange response (huge amount, wrong address) | Sweep amounts come from the store's own wallet, never from the exchange; the exchange's deposit address is validated for the coin's network on this server before anything is signed. Sell amounts are checked against the exchange balances and daily caps. Hedge *(planned)* |
| Deposit sent without a required tag or memo is not credited | Connectors return the tag with the address; sweeps refuse destinations that need one |
| Deposit below the exchange minimum is lost | Minimum deposits read from the exchange; a sweep waits until the exchange's share, after the network fee and cold wallet share, clears it |
| Deposit never credited, or credited without being sold | Each sent sweep is watched on the exchange (by transaction ID, on the exchange it was sent to, searched from when it was signed) for 24 hours, and never dropped on a failed lookup; an uncredited deposit is reported to the owner, and a credit of nothing is treated as an error. A credited deposit starts a convert run straight away, and the scheduled convert still sells anything that arrives later |
| Returned deposit swept back again and again, paying fees each time | A deposit the exchange fails or returns stops sweeping of that coin until the owner resumes it (which needs wallet permissions) |
| Flood of wallet events (e.g. a block confirming many transactions) | Events are coalesced per coin, and a store runs one sweep at a time |
| Sale fills far below value on a thin or manipulated market | No unpriced market orders: sells are immediate-or-cancel limits at a floor set a configurable percentage (default 2%) below the exchange's 24-hour average price, which a single trade can't drag down; when the best bid is below the floor, nothing is sent. Optional daily cap on value sold, kept per target asset and counted pessimistically when a fill is unknown (an order removed by hand counts in full). Dry run to observe before going live. In place for convert; sweeps and hedges *(planned)* |
| One store reads another's keys | Settings are keyed by store ID and only loaded for the authorized store. When stores share a wallet, a store waiting for another's unsettled sweep is told only that one exists; the other store is named in the server log, not in its activity |
| Data-protection keys lost after a migration | Decryption failure reads as "no key saved", and the owner re-enters it; nothing crashes or leaks |

## Out of scope

Compromise of the BTCPay server host or its admin accounts, and of the exchange itself. With host access an
attacker can already spend hot wallet funds.
