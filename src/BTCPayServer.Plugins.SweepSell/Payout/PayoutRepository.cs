#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BTCPayServer.Plugins.SweepSell.Settings;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.SweepSell.Payout;

/// <summary>Where payout keeps each store's settings and job state, apart from the trading settings.</summary>
public interface IPayoutRepository
{
    Task<PayoutSettings> GetAsync(string storeId);
    Task SaveAsync(string storeId, PayoutSettings settings);

    /// <summary>The payout settings of every store that has saved any.</summary>
    Task<IReadOnlyDictionary<string, PayoutSettings>> GetAllAsync();

    Task<PayoutState> GetStateAsync(string storeId);
    Task SaveStateAsync(string storeId, PayoutState state);
}

/// <summary>Store settings, which BTCPay's API does not return.</summary>
public class PayoutRepository(StoreRepository storeRepository) : IPayoutRepository
{
    public const string SettingName = "SweepSell.Payout";
    public const string StateName = "SweepSell.PayoutState";

    public async Task<PayoutSettings> GetAsync(string storeId) =>
        await storeRepository.GetSettingAsync<PayoutSettings>(storeId, SettingName) ?? new PayoutSettings();

    public Task SaveAsync(string storeId, PayoutSettings settings) =>
        storeRepository.UpdateSetting(storeId, SettingName, settings);

    public async Task<IReadOnlyDictionary<string, PayoutSettings>> GetAllAsync() =>
        (await storeRepository.GetSettingsAsync<PayoutSettings>(SettingName))
        .Where(s => s.Value is not null)
        .ToDictionary(s => s.Key, s => s.Value!);

    public async Task<PayoutState> GetStateAsync(string storeId) =>
        await storeRepository.GetSettingAsync<PayoutState>(storeId, StateName) ?? new PayoutState();

    public Task SaveStateAsync(string storeId, PayoutState state) =>
        storeRepository.UpdateSetting(storeId, StateName, state);
}
