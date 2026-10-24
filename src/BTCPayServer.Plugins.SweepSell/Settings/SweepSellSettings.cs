#nullable enable
namespace BTCPayServer.Plugins.SweepSell.Settings;

/// <summary>
/// A store's Sweep &amp; Sell settings, saved as a store setting. The exchange key and secret are kept
/// encrypted (<see cref="SecretProtector"/>): this object never holds them in plain text.
/// </summary>
public class SweepSellSettings
{
    /// <summary>Stops every feature of this store at once. Settings are kept.</summary>
    public bool Paused { get; set; }

    /// <summary>Id of the store's exchange connector, e.g. "kraken".</summary>
    public string? ExchangeId { get; set; }

    public string? ProtectedApiKey { get; set; }
    public string? ProtectedApiSecret { get; set; }

    public ConvertSettings Convert { get; set; } = new();

    public SweepSettings Sweep { get; set; } = new();

    public bool HasCredentials => !string.IsNullOrEmpty(ProtectedApiKey) && !string.IsNullOrEmpty(ProtectedApiSecret);
}
