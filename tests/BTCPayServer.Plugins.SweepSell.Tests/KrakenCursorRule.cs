namespace BTCPayServer.Plugins.SweepSell.Tests;

/// <summary>Which later-page requests a fake Kraken list refuses, as Kraken does with "invalid cursor request".</summary>
public enum KrakenCursorRule
{
    /// <summary>Live Kraken: a cursor sent with the list's filters (asset, method, start) is refused.</summary>
    FiltersRefused,
    /// <summary>The other way round: a bare cursor is refused, one sent with the filters is read.</summary>
    BareRefused
}

static class KrakenCursor
{
    public const string Refusal = """{"error":["EGeneral:Invalid arguments:invalid cursor request"]}""";

    /// <summary>True when <paramref name="body"/>, a later page's request, breaks <paramref name="rule"/>.</summary>
    public static bool Refuses(KrakenCursorRule rule, string body)
    {
        var fields = body.Split('&').Select(p => p.Split('=')[0]).ToHashSet();
        if (!fields.Contains("cursor") || body.Contains("cursor=true"))
            return false;
        var filtered = fields.Overlaps(["asset", "method", "start"]);
        return rule == KrakenCursorRule.FiltersRefused ? filtered : !filtered;
    }
}
