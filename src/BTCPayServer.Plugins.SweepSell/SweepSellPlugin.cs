#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Exchanges.Kraken;
using BTCPayServer.Plugins.SweepSell.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.SweepSell;

/// <summary>
/// Sweep store wallets to an exchange and sell what comes in. This first version holds the store settings,
/// the encrypted exchange key and the exchange connectors; the sweep, hedge and convert jobs build on them.
/// </summary>
public class SweepSellPlugin : BaseBTCPayServerPlugin
{
    public const string ViewsDirectory = "/Views/SweepSell";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.5" }
    ];

    public override void Execute(IServiceCollection services)
    {
        services.AddSingleton<SecretProtector>();
        services.AddSingleton<SweepSellSettingsRepository>();

        services.AddHttpClient(KrakenConnector.HttpClientName);
        services.AddSingleton<IExchangeConnector>(provider => new KrakenConnector(
            provider.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(KrakenConnector.HttpClientName)));
        services.AddSingleton<ExchangeRegistry>();

        services.AddUIExtension("store-integrations-nav", $"{ViewsDirectory}/StoreNavExtension.cshtml");
    }
}
