#region header

// AzureDdns.FunctionApp.Tests - FqdnResolverTests.cs
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
using AzureDdns.FunctionApp.Services;

#endregion

namespace AzureDdns.FunctionApp.Tests;

public sealed class FqdnResolverTests
{
  private readonly FqdnResolver _resolver = new ();

  [Fact]
  public void Resolve_ReturnsResolution_ForSimpleFqdn ()
  {
    var zones = BuildZones ("example.com");

    FqdnResolution? result = _resolver.Resolve (hostname: "home.example.com", zones: zones);

    Assert.NotNull (result);
    Assert.Equal (expected: "home", actual: result.Name);
    Assert.Equal (expected: "example.com", actual: result.Zone);
  }

  [Fact]
  public void Resolve_ReturnsApex_WhenHostnameEqualsZone ()
  {
    var zones = BuildZones ("example.com");

    FqdnResolution? result = _resolver.Resolve (hostname: "example.com", zones: zones);

    Assert.NotNull (result);
    Assert.Equal (expected: "@", actual: result.Name);
    Assert.Equal (expected: "example.com", actual: result.Zone);
  }

  [Fact]
  public void Resolve_UsesLongestZoneMatch_ForNestedZones ()
  {
    // Both "example.com" and "sub.example.com" are configured.
    // "a.sub.example.com" matches both, but the longer zone should win.
    var zones = BuildZones ("example.com", "sub.example.com");

    FqdnResolution? result = _resolver.Resolve (hostname: "a.sub.example.com", zones: zones);

    Assert.NotNull (result);
    Assert.Equal (expected: "a",             actual: result.Name);
    Assert.Equal (expected: "sub.example.com", actual: result.Zone);
  }

  [Fact]
  public void Resolve_ReturnsNull_WhenNoZoneMatches ()
  {
    var zones = BuildZones ("example.com");

    FqdnResolution? result = _resolver.Resolve (hostname: "home.other.com", zones: zones);

    Assert.Null (result);
  }

  [Fact]
  public void Resolve_ReturnsNull_ForEmptyHostname ()
  {
    var zones = BuildZones ("example.com");

    FqdnResolution? result = _resolver.Resolve (hostname: string.Empty, zones: zones);

    Assert.Null (result);
  }

  [Fact]
  public void Resolve_AcceptsTrailingDotOnHostname ()
  {
    var zones = BuildZones ("example.com");

    // FQDN with trailing dot is valid; trailing dot should be stripped before matching.
    FqdnResolution? result = _resolver.Resolve (hostname: "home.example.com.", zones: zones);

    Assert.NotNull (result);
    Assert.Equal (expected: "home", actual: result.Name);
  }

  [Fact]
  public void Resolve_IsCaseInsensitive_ForZonePortion ()
  {
    var zones = BuildZones ("example.com");

    // Hostname in uppercase; zone should still match.
    FqdnResolution? result = _resolver.Resolve (hostname: "HOME.EXAMPLE.COM", zones: zones);

    Assert.NotNull (result);
    // The record portion retains the original casing from the hostname input.
    Assert.Equal (expected: "HOME", actual: result.Name);
  }

  [Fact]
  public void Resolve_DoesNotMatchPartialLabel ()
  {
    // "fakeexample.com" shares a suffix with "example.com" but the match is not at a label
    // boundary, so it must not resolve.
    var zones = BuildZones ("example.com");

    FqdnResolution? result = _resolver.Resolve (hostname: "fakeexample.com", zones: zones);

    Assert.Null (result);
  }

  [Fact]
  public void Resolve_AcceptsTrailingDotOnConfiguredZone ()
  {
    // A zone key that accidentally has a trailing dot in the config should still match.
    var zones = new Dictionary<string, ZoneConfig> (StringComparer.OrdinalIgnoreCase)
                { ["example.com."] = new ZoneConfig { Ttl = 300 }, };

    FqdnResolution? result = _resolver.Resolve (hostname: "home.example.com", zones: zones);

    Assert.NotNull (result);
    Assert.Equal (expected: "home", actual: result.Name);
  }

  [Fact]
  public void Resolve_ReturnsDeepRecordName_ForMultiLabelSubdomain ()
  {
    var zones = BuildZones ("example.com");

    // Record name itself can be multi-label (e.g., "a.b").
    FqdnResolution? result = _resolver.Resolve (hostname: "a.b.example.com", zones: zones);

    Assert.NotNull (result);
    Assert.Equal (expected: "a.b", actual: result.Name);
  }

  // ── helpers ───────────────────────────────────────────────────────────────────────────────

  private static Dictionary<string, ZoneConfig> BuildZones (params string[] zoneNames)
    => zoneNames.ToDictionary (keySelector: name => name,
                               elementSelector: _ => new ZoneConfig { Ttl = 300 },
                               comparer: StringComparer.OrdinalIgnoreCase);
}
