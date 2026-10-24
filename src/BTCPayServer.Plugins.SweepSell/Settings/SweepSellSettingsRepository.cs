#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SweepSell.Conversion;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.SweepSell.Settings;

/// <summary>Where Sweep &amp; Sell keeps each store's settings and job state.</summary>
public interface ISweepSellRepository
{
    Task<SweepSellSettings> GetAsync(string storeId);
    Task SaveAsync(string storeId, SweepSellSettings settings);

    /// <summary>The settings of every store that has saved any.</summary>
    Task<IReadOnlyDictionary<string, SweepSellSettings>> GetAllAsync();

    Task<ConvertState> GetConvertStateAsync(string storeId);
    Task SaveConvertStateAsync(string storeId, ConvertState state);
}

/// <summary>Reads and writes a store's <see cref="SweepSellSettings"/>. Store settings are not returned by BTCPay's API.</summary>
public class SweepSellSettingsRepository(StoreRepository storeRepository) : ISweepSellRepository
{
    public const string SettingName = "SweepSell.Settings";
    public const string ConvertStateName = "SweepSell.ConvertState";

    public async Task<SweepSellSettings> GetAsync(string storeId) =>
        await storeRepository.GetSettingAsync<SweepSellSettings>(storeId, SettingName) ?? new SweepSellSettings();

    public Task SaveAsync(string storeId, SweepSellSettings settings) =>
        storeRepository.UpdateSetting(storeId, SettingName, settings);

    public async Task<IReadOnlyDictionary<string, SweepSellSettings>> GetAllAsync() =>
        (await storeRepository.GetSettingsAsync<SweepSellSettings>(SettingName))
        .Where(s => s.Value is not null)
        .ToDictionary(s => s.Key, s => s.Value!);

    public async Task<ConvertState> GetConvertStateAsync(string storeId) =>
        await storeRepository.GetSettingAsync<ConvertState>(storeId, ConvertStateName) ?? new ConvertState();

    public Task SaveConvertStateAsync(string storeId, ConvertState state) =>
        storeRepository.UpdateSetting(storeId, ConvertStateName, state);
}
