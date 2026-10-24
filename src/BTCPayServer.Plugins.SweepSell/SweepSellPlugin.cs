#nullable enable
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.SweepSell.Conversion;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Exchanges.Kraken;
using BTCPayServer.Plugins.SweepSell.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BTCPayServer.Plugins.SweepSell;

/// <summary>
/// Sweep store wallets to an exchange and sell what comes in. Holds the store settings, the encrypted
/// exchange key and the exchange connectors, and runs the convert job; sweep and hedge build on the same parts.
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
        services.AddSingleton<ISweepSellRepository>(provider => provider.GetRequiredService<SweepSellSettingsRepository>());
        services.TryAddSingleton(System.TimeProvider.System);

        services.AddHttpClient(KrakenConnector.HttpClientName);
        services.AddSingleton<IExchangeConnector>(provider => new KrakenConnector(
            provider.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(KrakenConnector.HttpClientName)));
        services.AddSingleton<ExchangeRegistry>();

        services.AddSingleton<ConvertService>();
        services.AddScheduledTask<ConvertTask>(System.TimeSpan.FromMinutes(1));

        services.AddUIExtension("store-integrations-nav", $"{ViewsDirectory}/StoreNavExtension.cshtml");
    }
}
