using Microsoft.Extensions.Configuration;

namespace ExternalHistoryStore;

/// <summary>
/// Sample tenant metadata bound from <c>appsettings.json</c>'s <c>Tenants</c> section.
/// Used by <see cref="TenantContextProvider"/> to inject per-call system context based
/// on the active tenant ID stamped on the workflow's incoming chat messages.
/// </summary>
public sealed record TenantInfo(string Id, string Name, string Tier, double SlaPercent, string Lang);

/// <summary>
/// Singleton in-memory directory of tenants. Loaded from configuration at host startup
/// so the agent factory and the context provider can both resolve it from DI.
/// </summary>
public sealed class TenantDirectory
{
    private readonly IReadOnlyDictionary<string, TenantInfo> _tenants;

    public TenantDirectory(IReadOnlyDictionary<string, TenantInfo> tenants)
    {
        _tenants = tenants ?? throw new ArgumentNullException(nameof(tenants));
    }

    public TenantInfo? TryGet(string tenantId) =>
        _tenants.TryGetValue(tenantId, out var t) ? t : null;

    public IEnumerable<TenantInfo> All => _tenants.Values;

    /// <summary>
    /// Reads the <c>Tenants</c> section of configuration into an in-memory directory.
    /// </summary>
    public static TenantDirectory LoadFromConfig(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var section = config.GetSection("Tenants");
        var dict = new Dictionary<string, TenantInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var child in section.GetChildren())
        {
            var id = child.GetValue<string>("Id") ?? child.Key;
            var name = child.GetValue<string>("Name") ?? id;
            var tier = child.GetValue<string>("Tier") ?? "Standard";
            var sla = child.GetValue<double?>("SlaPercent") ?? 99.0;
            var lang = child.GetValue<string>("Lang") ?? "en-US";

            dict[id] = new TenantInfo(id, name, tier, sla, lang);
        }

        if (dict.Count == 0)
        {
            throw new InvalidOperationException(
                "No tenants found in configuration. Ensure appsettings.json contains a 'Tenants' section.");
        }

        return new TenantDirectory(dict);
    }
}
