#nullable enable
using System.Collections.Generic;

namespace BTCPayServer.Plugins.SweepSell.Settings;

/// <summary>
/// Convert at the exchange: on a schedule, sell everything available of <see cref="Assets"/> for
/// <see cref="TargetAsset"/>, as soon as it clears the exchange's minimum order.
/// </summary>
public class ConvertSettings
{
    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 1440;
    public const decimal MinSlippageLimit = 0.1m;
    public const decimal MaxSlippageLimit = 20m;

    public bool Enabled { get; set; }

    /// <summary>Checks every order with the exchange without placing it. On until the owner turns it off.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Tickers to sell, e.g. DOGE and LTC.</summary>
    public List<string> Assets { get; set; } = [];

    public string TargetAsset { get; set; } = "BTC";

    public int IntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Lowest acceptable price, as a percentage below the market's 24-hour average. Orders are immediate-or-cancel limits
    /// at that floor: they never fill lower, and a market below it waits.
    /// </summary>
    public decimal MaxSlippagePercent { get; set; } = 2m;

    /// <summary>Most <see cref="TargetAsset"/> value sold per UTC day; 0 for no cap.</summary>
    public decimal DailyCap { get; set; }
}
