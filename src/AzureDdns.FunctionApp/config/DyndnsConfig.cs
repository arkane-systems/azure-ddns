#region header

// AzureDdns.FunctionApp - DyndnsConfig.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-03-30 10:16 PM

#endregion

namespace AzureDdns.FunctionApp.Config;

/// <summary>
///   Root configuration model loaded from <c>dyndns.json</c>.
/// </summary>
public sealed class DyndnsConfig
{
    /// <summary>
    ///   Zone definitions keyed by zone name (case-insensitive).
    /// </summary>
    public Dictionary<string, ZoneConfig> Zones { get; init; } = new (StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///   Clients that are allowed to authenticate against the DDNS endpoint.
    /// </summary>
    public List<ClientConfig> Clients { get; init; } = [];

    /// <summary>
    ///   Finds the configuration for a zone, tolerating formatting differences between the
    ///   requested zone and the configured key: both sides are compared case-insensitively
    ///   after trimming whitespace and a trailing dot (so <c>example.com.</c> matches a
    ///   configured <c>example.com</c> and vice versa). Both endpoints use this lookup.
    /// </summary>
    /// <param name="zone">The zone name to look up.</param>
    /// <param name="zoneConfig">The matching zone configuration, if any.</param>
    /// <returns><see langword="true" /> if a configured zone matches.</returns>
    public bool TryGetZone (string zone, out ZoneConfig? zoneConfig)
    {
        if (this.Zones.TryGetValue (key: zone, value: out zoneConfig))
            return true;

        string normalized = NormalizeZoneName (zone);

        foreach ((string configuredKey, ZoneConfig configuredZone) in this.Zones)
        {
            if (!string.Equals (a: NormalizeZoneName (configuredKey),
                                b: normalized,
                                comparisonType: StringComparison.OrdinalIgnoreCase))
                continue;

            zoneConfig = configuredZone;
            return true;
        }

        zoneConfig = null;
        return false;
    }

    /// <summary>
    ///   Canonical form used whenever zone names are compared: surrounding whitespace and a
    ///   trailing dot removed. Comparisons should additionally be case-insensitive.
    /// </summary>
    public static string NormalizeZoneName (string zone) => zone.Trim ().TrimEnd ('.');
}

/// <summary>
///   Per-zone configuration used during update operations.
/// </summary>
public sealed class ZoneConfig
{
    /// <summary>
    ///   DNS TTL in seconds to apply when records are upserted.
    /// </summary>
    public int Ttl { get; init; } = 300;
}

/// <summary>
///   Client authentication and authorization configuration entry.
/// </summary>
public sealed class ClientConfig
{
    /// <summary>
    ///   Logical client name sent by DDNS caller in the <c>client</c> query parameter.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    ///   Lowercase SHA-256 hash of the raw client key.
    /// </summary>
    public string KeyHash { get; init; } = string.Empty;

    /// <summary>
    ///   Allowed zone/record pairs this client may update.
    /// </summary>
    public List<AllowedRecordConfig> AllowedRecords { get; init; } = [];
}

/// <summary>
///   Authorization rule entry representing one permitted zone/record pair.
/// </summary>
public sealed class AllowedRecordConfig
{
    /// <summary>
    ///   DNS zone name this rule applies to.
    /// </summary>
    public string Zone { get; init; } = string.Empty;

    /// <summary>
    ///   Record name allowed in the zone. The value <c>*</c> allows any record name in the zone.
    /// </summary>
    public string Name { get; init; } = string.Empty;
}

/// <summary>
///   Runtime settings sourced from Function App environment variables.
/// </summary>
public sealed class RuntimeSettings
{
    /// <summary>
    ///   Subscription ID containing Azure DNS zones managed by this application.
    /// </summary>
    public string DnsSubscriptionId { get; set; } = string.Empty;

    /// <summary>
    ///   Resource group containing Azure DNS zones managed by this application.
    /// </summary>
    public string DnsResourceGroup { get; set; } = string.Empty;

    /// <summary>
    ///   Path to the DDNS configuration file (absolute or relative to app base directory).
    /// </summary>
    public string ConfigPath { get; set; } = "config/dyndns.json";

    /// <summary>
    ///   Enables temporary logging of all incoming request headers for IP diagnostics.
    /// </summary>
    public bool LogAllRequestHeadersForIpDiagnostics { get; set; }
}
