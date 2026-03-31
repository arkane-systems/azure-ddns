namespace AzureDdns.FunctionApp.Config;

public sealed class DyndnsConfig
{
    public Dictionary<string, ZoneConfig> Zones { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<ClientConfig> Clients { get; init; } = [];
}

public sealed class ZoneConfig
{
    public int Ttl { get; init; } = 300;
}

public sealed class ClientConfig
{
    public string Name { get; init; } = string.Empty;

    public string KeyHash { get; init; } = string.Empty;

    public List<AllowedRecordConfig> AllowedRecords { get; init; } = [];
}

public sealed class AllowedRecordConfig
{
    public string Zone { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
}

public sealed class RuntimeSettings
{
    public string DnsSubscriptionId { get; set; } = string.Empty;

    public string DnsResourceGroup { get; set; } = string.Empty;

    public string ConfigPath { get; set; } = "config/dyndns.json";
}
