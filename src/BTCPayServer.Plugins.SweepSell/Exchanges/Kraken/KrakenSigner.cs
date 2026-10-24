#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;

namespace BTCPayServer.Plugins.SweepSell.Exchanges.Kraken;

/// <summary>Kraken's private-endpoint signature: base64(HMAC-SHA512(base64-decoded secret, path + SHA256(nonce + body))).</summary>
public static class KrakenSigner
{
    public static string Sign(string apiSecret, string path, string nonce, string body)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(apiSecret);
        }
        catch (FormatException)
        {
            throw new ExchangeException("The Kraken private key is not valid base64. Paste it exactly as Kraken shows it.");
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nonce + body));
        var pathBytes = Encoding.UTF8.GetBytes(path);
        var message = new byte[pathBytes.Length + hash.Length];
        pathBytes.CopyTo(message, 0);
        hash.CopyTo(message, pathBytes.Length);
        return Convert.ToBase64String(HMACSHA512.HashData(key, message));
    }
}
