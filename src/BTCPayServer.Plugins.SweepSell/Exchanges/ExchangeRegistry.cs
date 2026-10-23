#nullable enable
using System.Collections.Generic;
using System.Linq;

namespace BTCPayServer.Plugins.SweepSell.Exchanges;

/// <summary>The exchanges a store can pick. Adding one = implementing <see cref="IExchangeConnector"/> and registering it in the plugin.</summary>
public class ExchangeRegistry(IEnumerable<IExchangeConnector> connectors)
{
    readonly Dictionary<string, IExchangeConnector> _byId = connectors.ToDictionary(c => c.Id);

    public IReadOnlyCollection<IExchangeConnector> All => _byId.Values;

    public IExchangeConnector? Find(string? id) => id is not null && _byId.TryGetValue(id, out var c) ? c : null;
}
