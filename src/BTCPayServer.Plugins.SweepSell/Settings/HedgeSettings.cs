#nullable enable
using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.SweepSell.Settings;

public enum HedgeTrigger
{
    /// <summary>When the invoice is paid in full, before its payments confirm (or later, if it skips straight to settled).</summary>
    Processing,
    /// <summary>When the invoice is settled.</summary>
    Settled
}

public enum HedgeAmount
{
    /// <summary>The BTC the invoice's payments brought in (on-chain and Lightning). Other coins are left to the convert job.</summary>
    BtcPaid,
    /// <summary>The invoice's whole price, in BTC at the invoice's own rate, however it was paid.</summary>
    InvoiceValue
}

/// <summary>
/// Hedge on payment: when an invoice reaches <see cref="Trigger"/>, sell its BTC on the exchange straight away, to lock in
/// the price. Each invoice is hedged once; amounts below the exchange's minimum order wait until they add up.
/// </summary>
public class HedgeSettings
{
    public bool Enabled { get; set; }

    /// <summary>Checks every order with the exchange without placing it. On until the owner turns it off.</summary>
    public bool DryRun { get; set; } = true;

    public HedgeTrigger Trigger { get; set; } = HedgeTrigger.Settled;

    public HedgeAmount Amount { get; set; } = HedgeAmount.BtcPaid;

    /// <summary>
    /// What to sell BTC for, by invoice currency (e.g. GBP → CAD when the exchange account holds CAD). A currency not
    /// listed sells into <see cref="DefaultMarket"/>, or into the invoice currency itself when that is empty.
    /// </summary>
    public Dictionary<string, string> Markets { get; set; } = new();

    public string? DefaultMarket { get; set; }

    /// <summary>Lowest acceptable price, as a percentage below the market's 24-hour average (see <see cref="ConvertSettings"/>).</summary>
    public decimal MaxSlippagePercent { get; set; } = 2m;

    /// <summary>Most BTC sold per UTC day; 0 for no cap. Invoices over the cap wait for the next day.</summary>
    public decimal DailyCap { get; set; }

    /// <summary>
    /// When hedging was turned on. Invoices created before it are never hedged, so turning it on doesn't sell for old
    /// invoices. Set when the settings are saved.
    /// </summary>
    public DateTimeOffset? EnabledSince { get; set; }

    /// <summary>The market <paramref name="invoiceCurrency"/> invoices sell BTC into.</summary>
    public string MarketFor(string invoiceCurrency) =>
        Markets.TryGetValue(invoiceCurrency.ToUpperInvariant(), out var market) ? market
        : string.IsNullOrWhiteSpace(DefaultMarket) ? invoiceCurrency.ToUpperInvariant()
        : DefaultMarket;
}
