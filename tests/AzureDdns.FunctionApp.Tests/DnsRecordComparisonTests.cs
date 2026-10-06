#region header

// AzureDdns.FunctionApp.Tests - DnsRecordComparisonTests.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-04-18 12:00 AM

#endregion

#region using

using System.Net;

using AzureDdns.FunctionApp.Services;

#endregion

namespace AzureDdns.FunctionApp.Tests;

public sealed class DnsRecordComparisonTests
{
  private static IPAddress Ip (string text) => IPAddress.Parse (text);

  [Fact]
  public void IsCurrent_ReturnsTrue_ForSingleMatchingAddressAndTtl ()
    => Assert.True (DnsRecordComparison.IsCurrent (existingAddresses: [Ip ("203.0.113.10")],
                                                   existingTtl: 300,
                                                   desiredAddress: Ip ("203.0.113.10"),
                                                   desiredTtl: 300));

  [Fact]
  public void IsCurrent_ReturnsTrue_ForEquivalentIpv6Spellings ()
    => Assert.True (DnsRecordComparison.IsCurrent (existingAddresses: [Ip ("2001:DB8:0:0:0:0:0:1")],
                                                   existingTtl: 60,
                                                   desiredAddress: Ip ("2001:db8::1"),
                                                   desiredTtl: 60));

  [Fact]
  public void IsCurrent_ReturnsFalse_WhenAddressDiffers ()
    => Assert.False (DnsRecordComparison.IsCurrent (existingAddresses: [Ip ("203.0.113.11")],
                                                    existingTtl: 300,
                                                    desiredAddress: Ip ("203.0.113.10"),
                                                    desiredTtl: 300));

  [Fact]
  public void IsCurrent_ReturnsFalse_WhenTtlDiffers_SoConfigChangesAreApplied ()
    => Assert.False (DnsRecordComparison.IsCurrent (existingAddresses: [Ip ("203.0.113.10")],
                                                    existingTtl: 3600,
                                                    desiredAddress: Ip ("203.0.113.10"),
                                                    desiredTtl: 300));

  [Fact]
  public void IsCurrent_ReturnsFalse_WhenTtlMissing ()
    => Assert.False (DnsRecordComparison.IsCurrent (existingAddresses: [Ip ("203.0.113.10")],
                                                    existingTtl: null,
                                                    desiredAddress: Ip ("203.0.113.10"),
                                                    desiredTtl: 300));

  [Fact]
  public void IsCurrent_ReturnsFalse_WhenRecordSetHasExtraAddresses_BecauseTheUpdateReplacesTheWholeSet ()
    => Assert.False (DnsRecordComparison.IsCurrent (existingAddresses: [Ip ("203.0.113.10"), Ip ("203.0.113.99")],
                                                    existingTtl: 300,
                                                    desiredAddress: Ip ("203.0.113.10"),
                                                    desiredTtl: 300));

  [Fact]
  public void IsCurrent_ReturnsFalse_ForEmptyRecordSet ()
    => Assert.False (DnsRecordComparison.IsCurrent (existingAddresses: [],
                                                    existingTtl: 300,
                                                    desiredAddress: Ip ("203.0.113.10"),
                                                    desiredTtl: 300));

  [Fact]
  public void IsCurrent_ReturnsFalse_ForNullAddressEntry ()
    => Assert.False (DnsRecordComparison.IsCurrent (existingAddresses: [null],
                                                    existingTtl: 300,
                                                    desiredAddress: Ip ("203.0.113.10"),
                                                    desiredTtl: 300));
}
