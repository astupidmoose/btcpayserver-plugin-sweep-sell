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

    /// <summary>The last traded price of one <paramref name="baseAsset"/> in <paramref name="quoteAsset"/>.</summary>
    Task<decimal> GetPriceAsync(string baseAsset, string quoteAsset, CancellationToken cancellationToken);

    /// <summary>Minimum order and amount precision of a market.</summary>
    Task<MarketRules> GetMarketRulesAsync(string baseAsset, string quoteAsset, CancellationToken cancellationToken);

    /// <summary>
    /// Sells <paramref name="amount"/> of <paramref name="baseAsset"/> for <paramref name="quoteAsset"/> at market.
    /// <paramref name="clientOrderId"/> is derived from the business fact (e.g. the invoice) and sent with the order,
    /// so after a lost response the caller can find out whether the order exists before it ever retries.
    /// With <paramref name="validateOnly"/>, the exchange checks the order without placing it (dry run).
    /// A placed order without an exchange order ID is an error, never a success.
    /// </summary>
    Task<OrderResult> SellAtMarketAsync(ExchangeCredentials credentials, string baseAsset, string quoteAsset, decimal amount,
        Guid clientOrderId, bool validateOnly, CancellationToken cancellationToken);

    /// <summary>Tries each permission Sweep &amp; Sell uses, without moving funds.</summary>
    Task<CredentialsCheck> TestCredentialsAsync(ExchangeCredentials credentials, CancellationToken cancellationToken);
}
