#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BTCPayServer.Plugins.SweepSell.Exchanges;

/// <summary>
/// Everything Sweep &amp; Sell needs from an exchange. Assets are named by their common ticker
/// (BTC, LTC, DOGE, XMR, CAD, USD...); each connector maps them to the exchange's own names.
/// A connector never withdraws: there is deliberately no call for it.
/// </summary>
public interface IExchangeConnector
{
    /// <summary>Stable id saved in store settings, e.g. "kraken".</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>What the exchange's API key needs, shown on the settings page.</summary>
    string KeyPermissionsHelp { get; }

    /// <summary>Spendable balances by asset: the total minus anything held by open orders. Zero balances are left out.</summary>
    Task<IReadOnlyDictionary<string, decimal>> GetAvailableBalancesAsync(ExchangeCredentials credentials, CancellationToken cancellationToken);

    /// <summary>
    /// Where to deposit <paramref name="asset"/>. With <paramref name="fresh"/>, a new address when the exchange allows it.
    /// Some assets also need a tag or memo (<see cref="DepositDestination.Tag"/>); a deposit without it is not credited.
    /// </summary>
    Task<DepositDestination> GetDepositAddressAsync(ExchangeCredentials credentials, string asset, bool fresh, CancellationToken cancellationToken);

    /// <summary>
    /// The smallest deposit of <paramref name="asset"/> the exchange credits, or 0 when it has none.
    /// A sweep that would send less to the exchange waits instead, so coins are never lost below it.
    /// </summary>
    Task<decimal> GetMinimumDepositAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken);

    /// <summary>Last trade, best bid and 24-hour average price of one <paramref name="baseAsset"/> in <paramref name="quoteAsset"/>.</summary>
    Task<MarketPrice> GetPriceAsync(string baseAsset, string quoteAsset, CancellationToken cancellationToken);

    /// <summary>Minimum order, amount precision and price precision of a market.</summary>
    Task<MarketRules> GetMarketRulesAsync(string baseAsset, string quoteAsset, CancellationToken cancellationToken);

    /// <summary>
    /// Sells up to <paramref name="amount"/> of <paramref name="baseAsset"/> for <paramref name="quoteAsset"/> right away, at
    /// <paramref name="limitPrice"/> or better (an immediate-or-cancel limit order): whatever can't fill at that price is
    /// cancelled, never left on the book or sold lower. Never an unpriced market order.
    /// <paramref name="clientOrderId"/> is derived from the business fact (e.g. the invoice) and sent with the order,
    /// so after a lost response <see cref="FindOrderAsync"/> tells whether the order exists. Exchanges may only enforce
    /// a client order ID's uniqueness among open orders, so a caller must look the order up before it ever retries.
    /// A live order must be unable to execute later than a minute after the call (e.g. with the exchange's order deadline),
    /// so an order still unlisted after the caller's grace period was never placed.
    /// With <paramref name="validateOnly"/>, the exchange checks the order without placing it (dry run).
    /// A placed order without an exchange order ID is an error, never a success.
    /// </summary>
    Task<OrderResult> SellAsync(ExchangeCredentials credentials, string baseAsset, string quoteAsset, decimal amount, decimal limitPrice,
        Guid clientOrderId, bool validateOnly, CancellationToken cancellationToken);

    /// <summary>
    /// The open and closed orders placed with <paramref name="clientOrderId"/>, or null when the exchange has none.
    /// A retry is only safe when this returns null.
    /// </summary>
    Task<OrderLookup?> FindOrderAsync(ExchangeCredentials credentials, Guid clientOrderId, CancellationToken cancellationToken);

    /// <summary>
    /// The exchange's record of an on-chain deposit of <paramref name="asset"/> sent in transaction <paramref name="txid"/>
    /// at <paramref name="since"/> or later, or null when it has none (yet). Looks through every deposit since then, however many.
    /// </summary>
    Task<DepositStatus?> FindDepositAsync(ExchangeCredentials credentials, string asset, string txid, DateTimeOffset since,
        CancellationToken cancellationToken);

    /// <summary>Tries each permission Sweep &amp; Sell uses, without moving funds.</summary>
    Task<CredentialsCheck> TestCredentialsAsync(ExchangeCredentials credentials, CancellationToken cancellationToken);
}
