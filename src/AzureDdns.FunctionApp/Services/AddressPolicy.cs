#region header

// AzureDdns.FunctionApp - AddressPolicy.cs
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
using System.Net.Sockets;

#endregion

namespace AzureDdns.FunctionApp.Services;

/// <summary>
///   Decides whether an address is acceptable to publish in public DNS.
/// </summary>
/// <remarks>
///   <para>
///     A DDNS update publishes an address on the public Internet, so addresses that can never be reached from
///     there are rejected: unspecified, loopback, link-local, multicast, reserved, private (RFC 1918), carrier-grade
///     NAT shared space, benchmarking, and for IPv6 anything outside global unicast (<c>2000::/3</c>), which
///     includes unique-local (<c>fc00::/7</c>). Publishing one is almost always a client or proxy mistake — for
///     example a router reporting its LAN address, or this app seeing only an internal proxy hop because no
///     forwarding header arrived — and would silently break the name.
///   </para>
///   <para>
///     The documentation ranges (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24, 2001:db8::/32, 3fff::/20) are
///     deliberately <em>allowed</em>: they are never routable, so they cannot misdirect traffic, and the
///     post-deployment smoke test publishes addresses from them on purpose.
///   </para>
///   <para>
///     IPv4-mapped IPv6 addresses should be normalized to IPv4 before this check (<see cref="IpResolver" /> does).
///   </para>
/// </remarks>
public static class AddressPolicy
{
  /// <summary>
  ///   Returns why <paramref name="address" /> must not be published, or <see langword="null" /> if it may be.
  /// </summary>
  public static string? RejectionReason (IPAddress address)
  {
    ArgumentNullException.ThrowIfNull (address);

    return address.AddressFamily switch
           {
             AddressFamily.InterNetwork => RejectIpv4 (address.GetAddressBytes ()),
             AddressFamily.InterNetworkV6 => RejectIpv6 (address.GetAddressBytes ()),
             _ => "unsupported address family",
           };
  }

  private static string? RejectIpv4 (byte[] b)
  {
    // Documentation ranges are checked first because 192.0.0.0/24 (below) sits next to 192.0.2.0/24.
    if (b is [192, 0, 2, _] or [198, 51, 100, _] or [203, 0, 113, _])
      return null;

    return b switch
           {
             [0, ..] => "unspecified or 'this network' (0.0.0.0/8)",
             [10, ..] => "private (10.0.0.0/8)",
             [100, >= 64 and <= 127, ..] => "carrier-grade NAT shared space (100.64.0.0/10)",
             [127, ..] => "loopback (127.0.0.0/8)",
             [169, 254, ..] => "link-local (169.254.0.0/16)",
             [172, >= 16 and <= 31, ..] => "private (172.16.0.0/12)",
             [192, 0, 0, _] => "IETF protocol assignments (192.0.0.0/24)",
             [192, 168, ..] => "private (192.168.0.0/16)",
             [198, 18 or 19, ..] => "benchmarking (198.18.0.0/15)",
             [>= 224 and <= 239, ..] => "multicast (224.0.0.0/4)",
             [>= 240, ..] => "reserved or broadcast (240.0.0.0/4)",
             _ => null,
           };
  }

  private static string? RejectIpv6 (byte[] b)
  {
    // Global unicast is 2000::/3: the top three bits are 001. This one test excludes unspecified (::), loopback
    // (::1), IPv4-compatible/mapped forms, NAT64, unique-local (fc00::/7), link-local (fe80::/10) and multicast.
    return (b[0] & 0xe0) == 0x20 ? null : "not global unicast (2000::/3)";
  }
}
