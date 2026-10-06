#region header

// AzureDdns.FunctionApp.Tests - AddressPolicyTests.cs
//
// Alistair J. R. Young
// Arkane Systems
//
// Copyright Arkane Systems 2012-2018.  All rights reserved.
//
// Created: 2026-10-06 6:00 PM

#endregion

#region using

using System.Net;

using AzureDdns.FunctionApp.Services;

#endregion

namespace AzureDdns.FunctionApp.Tests;

public sealed class AddressPolicyTests
{
  [Theory]
  [InlineData ("8.8.8.8")]
  [InlineData ("1.1.1.1")]
  [InlineData ("93.184.216.34")]
  [InlineData ("100.63.255.255")] // just below CGNAT 100.64/10
  [InlineData ("100.128.0.1")]    // just above CGNAT
  [InlineData ("172.15.255.255")] // just below 172.16/12
  [InlineData ("172.32.0.1")]     // just above 172.16/12
  [InlineData ("192.0.1.1")]      // between 192.0.0.0/24 and 192.0.2.0/24
  [InlineData ("192.169.0.1")]    // just above 192.168/16
  [InlineData ("198.17.255.255")] // just below benchmarking 198.18/15
  [InlineData ("198.20.0.1")]     // just above benchmarking
  [InlineData ("223.255.255.255")] // last address below multicast
  [InlineData ("2a02:1234:5678::5")]
  [InlineData ("2606:4700:4700::1111")]
  [InlineData ("2000::1")]
  [InlineData ("3fff:ffff::1")]
  public void RejectionReason_ReturnsNull_ForPubliclyRoutableAddresses (string text)
    => Assert.Null (AddressPolicy.RejectionReason (IPAddress.Parse (text)));

  [Theory]
  [InlineData ("192.0.2.1")]
  [InlineData ("198.51.100.200")]
  [InlineData ("203.0.113.5")]
  [InlineData ("2001:db8::1")]
  [InlineData ("3fff::1")]
  public void RejectionReason_AllowsDocumentationRanges_BecauseTheSmokeTestUsesThem (string text)
    => Assert.Null (AddressPolicy.RejectionReason (IPAddress.Parse (text)));

  [Theory]
  [InlineData ("0.0.0.0")]
  [InlineData ("0.1.2.3")]
  [InlineData ("10.0.0.1")]
  [InlineData ("10.255.255.255")]
  [InlineData ("100.64.0.1")]
  [InlineData ("100.127.255.255")]
  [InlineData ("127.0.0.1")]
  [InlineData ("127.255.255.254")]
  [InlineData ("169.254.1.1")]
  [InlineData ("172.16.0.1")]
  [InlineData ("172.31.255.255")]
  [InlineData ("192.0.0.1")]
  [InlineData ("192.168.1.1")]
  [InlineData ("198.18.0.1")]
  [InlineData ("198.19.255.255")]
  [InlineData ("224.0.0.1")]
  [InlineData ("239.255.255.255")]
  [InlineData ("240.0.0.1")]
  [InlineData ("255.255.255.255")]
  public void RejectionReason_RejectsNonPublicIpv4 (string text)
    => Assert.NotNull (AddressPolicy.RejectionReason (IPAddress.Parse (text)));

  [Theory]
  [InlineData ("::")]
  [InlineData ("::1")]
  [InlineData ("fe80::1")]
  [InlineData ("fd12:3456:789a::1")] // unique-local
  [InlineData ("fc00::1")]
  [InlineData ("ff02::1")]           // multicast
  [InlineData ("64:ff9b::1.2.3.4")]  // NAT64
  [InlineData ("1fff::1")]           // just below 2000::/3
  [InlineData ("4000::1")]           // just above 2000::/3
  public void RejectionReason_RejectsNonGlobalUnicastIpv6 (string text)
    => Assert.NotNull (AddressPolicy.RejectionReason (IPAddress.Parse (text)));

  [Fact]
  public void RejectionReason_ExplainsTheRejection ()
    => Assert.Contains (expectedSubstring: "10.0.0.0/8", actualString: AddressPolicy.RejectionReason (IPAddress.Parse ("10.1.2.3")));

  [Fact]
  public void RejectionReason_RejectsIpv4MappedIpv6_SoCallersMustNormalizeFirst ()
    => Assert.NotNull (AddressPolicy.RejectionReason (IPAddress.Parse ("::ffff:8.8.8.8")));
}
