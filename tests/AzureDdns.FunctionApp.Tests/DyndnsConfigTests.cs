#region header

// AzureDdns.FunctionApp.Tests - DyndnsConfigTests.cs
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

namespace AzureDdns.FunctionApp.Tests;

public sealed class DyndnsConfigTests
{
  private static DyndnsConfig BuildConfig (string zoneKey, int ttl = 120)
    => new () { Zones = { [zoneKey] = new ZoneConfig { Ttl = ttl }, }, };

  [Theory]
  [InlineData ("example.com",   "example.com")]
  [InlineData ("EXAMPLE.com",   "example.com")]
  [InlineData ("example.com.",  "example.com")]
  [InlineData ("example.com",   "example.com.")]
  [InlineData (" example.com ", "example.com")]
  [InlineData ("example.com",   " example.com. ")]
  public void TryGetZone_MatchesDespiteFormattingDifferences (string requested, string configuredKey)
  {
    DyndnsConfig config = BuildConfig (configuredKey);

    bool found = config.TryGetZone (zone: requested, zoneConfig: out ZoneConfig? zoneConfig);

    Assert.True (found);
    Assert.NotNull (zoneConfig);
    Assert.Equal (expected: 120, actual: zoneConfig.Ttl);
  }

  [Fact]
  public void TryGetZone_ReturnsFalse_WhenZoneNotConfigured ()
  {
    DyndnsConfig config = BuildConfig ("example.com");

    bool found = config.TryGetZone (zone: "other.com", zoneConfig: out ZoneConfig? zoneConfig);

    Assert.False (found);
    Assert.Null (zoneConfig);
  }

  [Fact]
  public void TryGetZone_DoesNotMatchParentOrSubdomain ()
  {
    DyndnsConfig config = BuildConfig ("example.com");

    Assert.False (config.TryGetZone (zone: "sub.example.com", zoneConfig: out _));
    Assert.False (config.TryGetZone (zone: "com",             zoneConfig: out _));
  }
}
