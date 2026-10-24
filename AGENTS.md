# Agent and review instructions

Sweep & Sell is a BTCPay Server plugin that moves real money: it sends coins from store hot wallets,
places orders on exchanges and, when the owner turns payout on, withdraws from the exchange to an account
the owner saved there. Review every change as if a mistake costs funds, because it can.

## Layout

- `src/BTCPayServer.Plugins.SweepSell`: the plugin.
  - `Exchanges/`: `IExchangeConnector` (trading), `IWithdrawalConnector` (payout) and one folder per exchange (`Kraken/`).
  - `Conversion/`, `Payout/`: the convert and payout jobs and their saved state.
  - `Settings/`: per-store settings and `SecretProtector`, which encrypts exchange credentials.
  - `Conversion/`: the convert job (sells exchange balances) and `PendingOrders`, the lookup of sells with an
    unknown outcome that every selling job shares.
  - `Hedging/`: the hedge job (sells each paid invoice's BTC). `IStoreInvoices` is the invoice boundary.
  - `Sweeping/`: the sweep job. `SweepBuilder` builds and signs the transaction (pure NBitcoin);
    `IStoreWallets` is the wallet boundary, implemented with BTCPay's services in `BtcPayStoreWallets`.
  - `Controllers/`, `Views/SweepSell/`: the store settings page.
- `tests/BTCPayServer.Plugins.SweepSell.Tests`: xUnit tests. Exchange calls use `FakeHttp`, jobs use the fakes in
  `Fakes.cs` (`FakeExchange`, `FakeWallets`, `FakeInvoices`); nothing reaches a real exchange or node. Sweep transactions are
  built and signed on regtest with invented keys.
- `submodules/btcpayserver`: BTCPay Server, pinned. Don't change it in a feature PR.
- `docs/threat-model.md`: assets, threats and mitigations. Update it when a change adds an attack surface.

## Commands

```bash
git submodule update --init --recursive
dotnet build src/BTCPayServer.Plugins.SweepSell
dotnet test --project tests/BTCPayServer.Plugins.SweepSell.Tests
```

## Rules (reviewers: flag any break as high severity)

1. **No secrets or real-world data in the repository.** No API keys, private keys, real wallet or
   deposit addresses, store IDs, server host names, balances or order IDs, in code, tests, docs, PR
   text or commit messages. Test data is invented (`test-api-key`, `bc1qexample...`).
2. **Credentials are encrypted at rest.** Exchange keys are stored only through `SecretProtector`
   (BTCPay's data-protection key ring), never in payment method configs or anything BTCPay's API
   returns, and are never logged, rendered back into a page or put in an exception message.
3. **Trading never withdraws; payout only pays out to destinations saved in the exchange.**
   `IExchangeConnector` has no withdrawal call, and none may be added: the trading key is asked for
   without withdrawal permission, and its key test fails if it can withdraw. Fiat payout is the only
   feature that withdraws. It goes through `IWithdrawalConnector` with its own key, which must not be
   able to trade (its key test fails if it can) and is refused if it is the trading key. A withdrawal
   only names a destination the owner already saved (and the exchange verified) in the exchange account.
   No code may add or change a destination at the exchange, or send to an address typed into or
   computed by the plugin.
4. **Every money-moving action is idempotent.** A sweep, hedge, sale or payout is recorded under a key
   naming the business fact (e.g. the invoice ID) before or atomically with acting, so a restart, retry or
   duplicate event never sends or sells twice. Where the exchange takes no client ID (withdrawals), a lost
   response is looked up in the exchange's history and never retried blindly.
5. **Every money-moving feature has a dry run** that does everything except broadcast, place the order
   or withdraw (exchanges' validate-only order calls and withdrawal quotes where they exist), and
   respects the store's pause switch.
6. **Respect exchange limits.** Read minimum order sizes, amount precision and minimum deposits from
   the exchange; never hardcode them. Below a minimum, wait and retry later rather than send or sell.
7. **Fail loudly, not silently.** Errors are logged with context (store, feature, asset, amount) and
   surfaced to the store owner. Never swallow an exchange error and continue as if it worked.
8. **Amounts are `decimal`,** rounded down to the market's precision. Never `double` for money.
9. **Store isolation.** A store's settings, keys and wallets are only reachable by users with
   `CanModifyStoreSettings` on that store. Anything that decides what a sweep spends or where it goes also
   needs BTCPay's wallet signing and broadcasting permissions on the store.
10. **Every sale has a price floor.** Never send an unpriced market order. Sells are
    immediate-or-cancel limit orders at a floor derived from a reference price one trade can't move
    (e.g. the 24-hour average), and money-moving features offer a daily cap.

## Working on a change

- One feature per PR, from a branch off `main`. Say in the PR what changed, how it was tested and
  what could not be tested (e.g. needs a live exchange or regtest node).
- Add or update tests with every behaviour change. New exchange connectors come with tests for
  signing, asset mapping, balances, deposit addresses, limits and error handling.
- Keep user-facing text plain and specific; it is shown to store owners, not developers.
