using BTCPayServer.Plugins.SweepSell.Conversion;
using BTCPayServer.Plugins.SweepSell.Exchanges;
using BTCPayServer.Plugins.SweepSell.Settings;
using BTCPayServer.Plugins.SweepSell.Sweeping;

namespace BTCPayServer.Plugins.SweepSell.Tests;

sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

sealed class MemoryRepository : ISweepSellRepository
{
    public System.Collections.Concurrent.ConcurrentDictionary<string, SweepSellSettings> Settings { get; } = new();
    public System.Collections.Concurrent.ConcurrentDictionary<string, ConvertState> States { get; } = new();

    public Task<SweepSellSettings> GetAsync(string storeId) =>
        Task.FromResult(Settings.TryGetValue(storeId, out var s) ? Copy(s) : new SweepSellSettings());
    public Task SaveAsync(string storeId, SweepSellSettings settings) { Settings[storeId] = settings; return Task.CompletedTask; }
    public Task<IReadOnlyDictionary<string, SweepSellSettings>> GetAllAsync() => Task.FromResult<IReadOnlyDictionary<string, SweepSellSettings>>(Settings);

    // Stored as copies, like a database, so a test sees only what was saved.
    public Task<ConvertState> GetConvertStateAsync(string storeId) =>
        Task.FromResult(States.TryGetValue(storeId, out var s) ? Copy(s) : new ConvertState());

    public Task SaveConvertStateAsync(string storeId, ConvertState state) { States[storeId] = Copy(state); return Task.CompletedTask; }

    public System.Collections.Concurrent.ConcurrentDictionary<string, SweepState> SweepStates { get; } = new();

    public Task<SweepState> GetSweepStateAsync(string storeId) =>
        Task.FromResult(SweepStates.TryGetValue(storeId, out var s) ? Copy(s) : new SweepState());

    public Task SaveSweepStateAsync(string storeId, SweepState state) { SweepStates[storeId] = Copy(state); return Task.CompletedTask; }

    static T Copy<T>(T s) => Newtonsoft.Json.JsonConvert.DeserializeObject<T>(Newtonsoft.Json.JsonConvert.SerializeObject(s))!;
}

/// <summary>
/// An exchange with invented markets: every X/BTC pair has a 0.05 minimum, 8 amount and 8 price decimals, and trades
/// at 0.001 (last, bid and 24-hour average) unless a test changes <see cref="Price"/>. Live sells fill
/// <see cref="FillRatio"/> of the amount.
/// </summary>
sealed class FakeExchange : IExchangeConnector
{
    public const string ExchangeId = "fake";
    public string Id => ExchangeId;
    public string Name => "Fake Exchange";
    public string KeyPermissionsHelp => "";

    public Dictionary<string, decimal> Balances { get; } = new();
    public Dictionary<Guid, OrderLookup> Found { get; } = new();
    public List<(string Base, string Quote, decimal Amount, bool ValidateOnly, Guid Id, decimal Limit)> Orders { get; } = [];
    public MarketPrice Price { get; set; } = new(0.001m, 0.001m, 0.001m);
    public decimal FillRatio { get; set; } = 1m;
    public bool FillUnknown { get; set; }
    public List<string> Calls { get; } = [];
    public Exception? BalanceError { get; set; }
    public Action? OnSell { get; set; }
    /// <summary>Sells of a coin listed here wait for its task.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, Task> SellGates { get; } = new();
    public Exception? FindError { get; set; }
    public Action? OnPrice { get; set; }

    public Task<IReadOnlyDictionary<string, decimal>> GetAvailableBalancesAsync(ExchangeCredentials credentials, CancellationToken cancellationToken)
    {
        Calls.Add("balances");
        if (BalanceError is not null)
            throw BalanceError;
        return Task.FromResult<IReadOnlyDictionary<string, decimal>>(new Dictionary<string, decimal>(Balances));
    }

    public Task<MarketRules> GetMarketRulesAsync(string baseAsset, string quoteAsset, CancellationToken cancellationToken) =>
        Task.FromResult(new MarketRules(baseAsset, quoteAsset, 0.05m, 0m, 8, 8));

    public Task<MarketPrice> GetPriceAsync(string baseAsset, string quoteAsset, CancellationToken cancellationToken)
    {
        OnPrice?.Invoke();
        return Task.FromResult(Price);
    }

    public async Task<OrderResult> SellAsync(ExchangeCredentials credentials, string baseAsset, string quoteAsset, decimal amount, decimal limitPrice,
        Guid clientOrderId, bool validateOnly, CancellationToken cancellationToken)
    {
        lock (Orders)
        {
            Calls.Add("sell");
            Orders.Add((baseAsset, quoteAsset, amount, validateOnly, clientOrderId, limitPrice));
        }
        await SellGates.GetValueOrDefault(baseAsset, Task.CompletedTask);
        OnSell?.Invoke();
        if (!validateOnly && !FillUnknown)
            Found[clientOrderId] = new OrderLookup(["OAAAAA-BBBBB-CCCCCC"], Open: false, ExecutedAmount: amount * FillRatio);
        return new OrderResult(!validateOnly, validateOnly ? [] : ["OAAAAA-BBBBB-CCCCCC"], $"sell {amount} {baseAsset}{quoteAsset} @ limit {limitPrice}");
    }

    public Task<OrderLookup?> FindOrderAsync(ExchangeCredentials credentials, Guid clientOrderId, CancellationToken cancellationToken)
    {
        Calls.Add("find");
        if (FindError is not null)
            throw FindError;
        return Task.FromResult(Found.GetValueOrDefault(clientOrderId));
    }

    public DepositDestination DepositAddress { get; set; } = new("exchange-deposit-address", null);
    public decimal MinimumDeposit { get; set; } = 0.0001m;
    public Dictionary<string, DepositStatus> Deposits { get; } = new();
    public Exception? DepositError { get; set; }
    public Exception? DepositAddressError { get; set; }

    public Task<DepositDestination> GetDepositAddressAsync(ExchangeCredentials credentials, string asset, bool fresh, CancellationToken cancellationToken)
    {
        Calls.Add("deposit-address");
        if (DepositAddressError is not null)
            throw DepositAddressError;
        return Task.FromResult(DepositAddress);
    }

    public Task<decimal> GetMinimumDepositAsync(ExchangeCredentials credentials, string asset, CancellationToken cancellationToken) =>
        Task.FromResult(MinimumDeposit);

    public List<DateTimeOffset> DepositSearchesSince { get; } = [];
    public Action? OnFindDeposit { get; set; }

    public Task<DepositStatus?> FindDepositAsync(ExchangeCredentials credentials, string asset, string txid, DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        Calls.Add("find-deposit");
        DepositSearchesSince.Add(since);
        OnFindDeposit?.Invoke();
        if (DepositError is not null)
            throw DepositError;
        return Task.FromResult(Deposits.GetValueOrDefault(txid));
    }

    public Task<CredentialsCheck> TestCredentialsAsync(ExchangeCredentials credentials, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>
/// Store wallets without a node. Every sweep pays <see cref="Fee"/> and, once a broadcast is accepted, leaves the wallet
/// with its change, like a wallet that no longer lists spent coins. Addresses starting with "bad" are invalid, and a cold
/// destination starting with "xpub" derives "{destination}/{index}".
/// </summary>
sealed class FakeWallets : IStoreWallets
{
    public const decimal Fee = 0.0001m;

    /// <summary>Balances by "{storeId}:{code}"; a store with no entry for a coin has no wallet for it.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, decimal> Balances { get; } = new();

    /// <summary>Wallet keys by "{storeId}:{code}" when it differs from the store's own (a shared wallet).</summary>
    public Dictionary<string, string> Keys { get; } = new();

    public HashSet<string> Cold { get; } = [];
    public List<SweepRequest> Built { get; } = [];
    public List<string> Broadcasts { get; } = [];
    public Dictionary<string, TransactionStatus> Statuses { get; } = new();

    /// <summary>What the next broadcasts do, in order; once empty, every broadcast is accepted.</summary>
    public Queue<Func<BroadcastOutcome>> BroadcastResults { get; } = new();

    public Func<Task>? OnBuild { get; set; }
    readonly Dictionary<string, (string Wallet, decimal Spent)> _signed = new();
    int _txids;

    public Task<IReadOnlyList<string>> GetCryptoCodesAsync(string storeId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(Balances.Keys.Where(k => k.StartsWith(storeId + ":")).Select(k => k[(storeId.Length + 1)..]).Order().ToList());

    public Task<StoreWallet?> GetWalletAsync(string storeId, string cryptoCode, CancellationToken cancellationToken)
    {
        var id = $"{storeId}:{cryptoCode}";
        return Task.FromResult(Balances.ContainsKey(id) ? new StoreWallet(storeId, cryptoCode, Keys.GetValueOrDefault(id, id), !Cold.Contains(id)) : null);
    }

    public Task<decimal> GetBalanceAsync(StoreWallet wallet, bool includeUnconfirmed, CancellationToken cancellationToken) =>
        Task.FromResult(Balances[$"{wallet.StoreId}:{wallet.CryptoCode}"]);

    public string? CheckAddress(string cryptoCode, string address) =>
        address.StartsWith("bad") ? $"\"{address}\" isn't a {cryptoCode} address on this server's network." : null;

    public string? CheckColdDestination(string cryptoCode, string destination) => CheckAddress(cryptoCode, destination);

    public (string Address, bool Derived) GetColdAddress(string cryptoCode, string destination, int index) =>
        destination.StartsWith("xpub") ? ($"{destination}/{index}", true) : (destination, false);

    public async Task<SignedSweep> BuildAsync(StoreWallet wallet, SweepRequest request, CancellationToken cancellationToken)
    {
        if (OnBuild is not null)
            await OnBuild();
        lock (Built)
            Built.Add(request);
        var id = $"{wallet.StoreId}:{wallet.CryptoCode}";
        var swept = Math.Min(request.Amount ?? decimal.MaxValue, Balances[id]);
        var toCold = request.ColdAddress is null ? 0 : Math.Floor(swept * request.ColdPercent / 100m * 100_000_000m) / 100_000_000m;
        var toExchange = swept - toCold - Fee;
        if (toExchange <= 0)
            throw new SweepException("The coins don't cover the outputs and the network fee.");
        var txid = Interlocked.Increment(ref _txids).ToString("x64");
        lock (_signed)
            _signed["signed-" + txid] = (id, swept);
        return new SignedSweep(txid, "signed-" + txid, new SweepAmounts(toExchange, toCold, Fee, Balances[id] - swept), Balances[id]);
    }

    public Task<BroadcastOutcome> BroadcastAsync(string cryptoCode, string hex, CancellationToken cancellationToken)
    {
        Func<BroadcastOutcome>? next;
        lock (Broadcasts)
        {
            Broadcasts.Add(hex);
            BroadcastResults.TryDequeue(out next);
        }
        var outcome = next?.Invoke() ?? new BroadcastOutcome(true, null);
        lock (_signed)
        {
            if (outcome.Accepted && _signed.Remove(hex, out var spend))
                Balances[spend.Wallet] -= spend.Spent;
        }
        return Task.FromResult(outcome);
    }

    public Task<TransactionStatus> GetStatusAsync(string cryptoCode, string txid, CancellationToken cancellationToken) =>
        Task.FromResult(Statuses.GetValueOrDefault(txid, TransactionStatus.Unknown));
}
