#region header

// AzureDdns.FunctionApp - FqdnResolver.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-04-18 12:00 AM

#endregion

#region using

using AzureDdns.FunctionApp.Config;

#endregion

namespace AzureDdns.FunctionApp.Services;

public interface IFqdnResolver
{
  /// <summary>
  ///   Resolves a fully-qualified domain name to the matching configured DNS zone and relative record name.
  /// </summary>
  /// <param name="hostname">Fully-qualified domain name to resolve (trailing dot accepted).</param>
  /// <param name="zones">Configured DNS zones keyed by zone name.</param>
  /// <returns>
  ///   A <see cref="FqdnResolution" /> containing the matched zone and relative record name;
  ///   or <see langword="null" /> when no configured zone matches the hostname.
  /// </returns>
  FqdnResolution? Resolve (string hostname, IReadOnlyDictionary<string, ZoneConfig> zones);
}

/// <summary>
///   Result of a successful FQDN-to-zone resolution.
/// </summary>
/// <param name="Zone">Matched DNS zone name (no trailing dot, original config casing).</param>
/// <param name="Name">Relative record name within the zone; <c>@</c> for the zone apex.</param>
public sealed record FqdnResolution (string Zone, string Name);

/// <summary>
///   Resolves FQDNs to configured zone/record name pairs using longest-suffix zone matching.
/// </summary>
/// <remarks>
///   Longest-suffix matching ensures that a hostname like <c>a.sub.example.com</c> is attributed
///   to the most specific configured zone (e.g. <c>sub.example.com</c>) rather than a shallower
///   parent zone (e.g. <c>example.com</c>). Zone names in the config are matched case-insensitively
///   and trailing dots are normalised away before comparison.
/// </remarks>
public sealed class FqdnResolver : IFqdnResolver
{
  /// <inheritdoc />
  public FqdnResolution? Resolve (string hostname, IReadOnlyDictionary<string, ZoneConfig> zones)
  {
    string normalized = hostname.Trim ().TrimEnd ('.');

    if (string.IsNullOrWhiteSpace (normalized))
      return null;

    // Select the longest configured zone name whose label-boundary-aligned suffix matches the
    // hostname.  Matching at a label boundary means the match must be preceded by a dot
    // (subdomain) or be the entire hostname (apex) — this prevents "fakeexample.com" from
    // matching the "example.com" zone.
    string? bestZone = null;

    foreach (string configZone in zones.Keys)
    {
      string zone = configZone.Trim ().TrimEnd ('.');

      if (string.IsNullOrWhiteSpace (zone))
        continue;

      bool isApex      = string.Equals (a: normalized, b: zone, comparisonType: StringComparison.OrdinalIgnoreCase);
      bool isSubdomain = normalized.EndsWith ("." + zone, StringComparison.OrdinalIgnoreCase);

      if (!isApex && !isSubdomain)
        continue;

      if (bestZone is null || zone.Length > bestZone.Length)
        bestZone = zone;
    }

    if (bestZone is null)
      return null;

    // Apex: hostname exactly equals the zone name -> record name is the conventional "@".
    // Subdomain: strip the ".<zone>" suffix to obtain the relative record name.
    string name = string.Equals (a: normalized, b: bestZone, comparisonType: StringComparison.OrdinalIgnoreCase)
                    ? "@"
                    : normalized[..^(bestZone.Length + 1)];

    return new FqdnResolution (Zone: bestZone, Name: name);
  }
}
