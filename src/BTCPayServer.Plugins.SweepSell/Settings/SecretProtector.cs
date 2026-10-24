#nullable enable
using System.Security.Cryptography;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.SweepSell.Settings;

/// <summary>
/// Encrypts exchange credentials with BTCPay's data-protection key ring before they are saved, so the
/// database alone never reveals them. Plain text exists only in memory while a request or job runs.
/// </summary>
public class SecretProtector(IDataProtectionProvider provider)
{
    // Changing the purpose makes every saved key unreadable; bump the version only with a migration.
    const string Purpose = "BTCPayServer.Plugins.SweepSell.ExchangeCredentials.v1";
    readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(string plainText) => _protector.Protect(plainText);

    public string? Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
            return null;
        try
        {
            return _protector.Unprotect(protectedText);
        }
        catch (CryptographicException)
        {
            // The key ring changed (e.g. a restored database on a new server): the store must re-enter its key.
            return null;
        }
    }

    public ExchangeCredentials? Credentials(SweepSellSettings settings) => Credentials(settings.ProtectedApiKey, settings.ProtectedApiSecret);

    /// <summary>The withdraw-only payout key.</summary>
    public ExchangeCredentials? Credentials(PayoutSettings settings) => Credentials(settings.ProtectedApiKey, settings.ProtectedApiSecret);

    ExchangeCredentials? Credentials(string? protectedKey, string? protectedSecret) =>
        Unprotect(protectedKey) is { } key && Unprotect(protectedSecret) is { } secret
            ? new ExchangeCredentials(key, secret)
            : null;
}
