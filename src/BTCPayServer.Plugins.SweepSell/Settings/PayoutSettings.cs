#nullable enable
namespace BTCPayServer.Plugins.SweepSell.Settings;

/// <summary>
/// Fiat payout: on a schedule, withdraw a store's balance of <see cref="Asset"/> on the exchange to a withdrawal destination
/// the owner already saved there (<see cref="Destination"/>, by name). Saved apart from <see cref="SweepSellSettings"/>,
/// with its own withdraw-only key, so the trading key never needs withdrawal permission. The key is kept encrypted
/// (<see cref="SecretProtector"/>).
/// </summary>
public class PayoutSettings
{
    public const int MinIntervalMinutes = 15;
    public const int MaxIntervalMinutes = 10080;

    public bool Enabled { get; set; }

    /// <summary>Gets a quote from the exchange for each withdrawal without sending it. On until the owner turns it off.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Id of the exchange's withdrawal connector, e.g. "kraken".</summary>
    public string? ExchangeId { get; set; }

    public string? ProtectedApiKey { get; set; }
    public string? ProtectedApiSecret { get; set; }

    /// <summary>Ticker to withdraw, e.g. USD.</summary>
    public string Asset { get; set; } = "";

    /// <summary>Name of a withdrawal destination already saved and verified in the exchange account.</summary>
    public string Destination { get; set; } = "";

    /// <summary>Smallest withdrawal, in <see cref="Asset"/>: less waits on the exchange until more arrives.</summary>
    public decimal Minimum { get; set; }

    /// <summary>Always left on the exchange, in <see cref="Asset"/>.</summary>
    public decimal KeepBalance { get; set; }

    /// <summary>Most withdrawn per UTC day, in <see cref="Asset"/>. Required: payout never runs without a cap.</summary>
    public decimal DailyCap { get; set; }

    /// <summary>Highest fee per withdrawal, in <see cref="Asset"/>. 0 allows only free withdrawals.</summary>
    public decimal MaxFee { get; set; }

    public int IntervalMinutes { get; set; } = 1440;

    public bool HasCredentials => !string.IsNullOrEmpty(ProtectedApiKey) && !string.IsNullOrEmpty(ProtectedApiSecret);
}
