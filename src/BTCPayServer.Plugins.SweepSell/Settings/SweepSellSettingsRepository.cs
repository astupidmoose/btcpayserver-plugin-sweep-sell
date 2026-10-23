#nullable enable
using System.Threading.Tasks;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.SweepSell.Settings;

/// <summary>Reads and writes a store's <see cref="SweepSellSettings"/>. Store settings are not returned by BTCPay's API.</summary>
public class SweepSellSettingsRepository(StoreRepository storeRepository)
{
    public const string SettingName = "SweepSell.Settings";

    public async Task<SweepSellSettings> GetAsync(string storeId) =>
        await storeRepository.GetSettingAsync<SweepSellSettings>(storeId, SettingName) ?? new SweepSellSettings();

    public Task SaveAsync(string storeId, SweepSellSettings settings) =>
        storeRepository.UpdateSetting(storeId, SettingName, settings);
}
