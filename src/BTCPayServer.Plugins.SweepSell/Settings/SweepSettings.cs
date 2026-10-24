#nullable enable
using System.Collections.Generic;

namespace BTCPayServer.Plugins.SweepSell.Settings;

/// <summary>
/// Sweep rules: send a store's hot wallet balance to the exchange (and optionally a share to a cold wallet) as soon as
/// it passes a minimum, then convert it at the exchange as soon as the deposit is credited.
/// </summary>
public class SweepSettings
{
    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 1440;
    public const int MinFeeTargetBlocks = 1;
    public const int MaxFeeTargetBlocks = 144;

    public bool Enabled { get; set; }

    /// <summary>Builds and signs each sweep without broadcasting it. On until the owner turns it off.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>
    /// How often every wallet is checked even without a new transaction. New transactions to the wallet trigger a check
    /// straight away; this catches anything missed (e.g. while the server was down).
    /// </summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>Confirmation target for the network fee, in blocks.</summary>
    public int FeeTargetBlocks { get; set; } = 6;

    /// <summary>One rule per coin; a coin without an enabled rule is never swept.</summary>
    public List<SweepRule> Rules { get; set; } = [];
}

/// <summary>How one coin's hot wallet is swept.</summary>
public class SweepRule
{
    public string CryptoCode { get; set; } = "";

    public bool Enabled { get; set; }

    /// <summary>Sweep once the wallet holds at least this much (in the coin). The exchange's minimum deposit also applies.</summary>
    public decimal MinimumBalance { get; set; }

    /// <summary>Share of each sweep sent to <see cref="ColdDestination"/> instead of the exchange, from 0 to under 100.</summary>
    public decimal ColdPercent { get; set; }

    /// <summary>
    /// A fixed address, or an extended public key (in BTCPay's derivation scheme format) that gives a new receive address
    /// for every sweep, starting at <see cref="ColdStartIndex"/>.
    /// </summary>
    public string? ColdDestination { get; set; }

    /// <summary>First receive address index used for an extended public key, e.g. to skip addresses already used.</summary>
    public int ColdStartIndex { get; set; }

    /// <summary>Most of the coin swept per UTC day; 0 for no cap. A larger balance is swept in parts, with change.</summary>
    public decimal DailyCap { get; set; }

    /// <summary>Also sweep coins that are not confirmed yet. Off by default: only confirmed coins are swept.</summary>
    public bool IncludeUnconfirmed { get; set; }
}
