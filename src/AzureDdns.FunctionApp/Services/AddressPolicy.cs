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
///     <em>allowed by default</em>: they are never routable, so they cannot misdirect traffic, and the
///     post-deployment smoke test publishes addresses from them on purpose. Deployments that prefer to refuse them
///     too set <c>ALLOW_DOCUMENTATION_ADDRESSES=false</c> (<see cref="Config.RuntimeSettings.AllowDocumentationAddresses" />);
///     the smoke test then needs real addresses supplied with <c>-TestIpv4</c> / <c>-TestIpv6</c>.
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
  /// <param name="address">The address to check.</param>
  /// <param name="allowDocumentation">
  ///   Whether the documentation ranges are acceptable (the default). When <see langword="false" /> they are
  ///   rejected like any other unroutable range.
  /// </param>
  public static string? RejectionReason (IPAddress address, bool allowDocumentation = true)
  {
    ArgumentNullException.ThrowIfNull (address);

    byte[] bytes = address.GetAddressBytes ();

    return address.AddressFamily switch
           {
             AddressFamily.InterNetwork => RejectIpv4 (b: bytes, allowDocumentation: allowDocumentation),
             AddressFamily.InterNetworkV6 => RejectIpv6 (b: bytes, allowDocumentation: allowDocumentation),
             _ => "unsupported address family",
           };
  }

  private static string? RejectIpv4 (byte[] b, bool allowDocumentation)
  {
    // Documentation ranges are decided first because 192.0.0.0/24 (below) sits next to 192.0.2.0/24.
    string? documentation = b switch
                            {
                              [192, 0, 2, _] => "documentation range (192.0.2.0/24)",
                              [198, 51, 100, _] => "documentation range (198.51.100.0/24)",
                              [203, 0, 113, _] => "documentation range (203.0.113.0/24)",
                              _ => null,
                            };

    if (documentation is not null)
      return allowDocumentation ? null : documentation;

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

  private static string? RejectIpv6 (byte[] b, bool allowDocumentation)
  {
    // Global unicast is 2000::/3: the top three bits are 001. This one test excludes unspecified (::), loopback
    // (::1), IPv4-compatible/mapped forms, NAT64, unique-local (fc00::/7), link-local (fe80::/10) and multicast.
    if ((b[0] & 0xe0) != 0x20)
      return "not global unicast (2000::/3)";

    // 2001:db8::/32 (RFC 3849) and 3fff::/20 (RFC 9637) are the IPv6 documentation prefixes.
    string? documentation = b switch
                            {
                              [0x20, 0x01, 0x0d, 0xb8, ..] => "documentation range (2001:db8::/32)",
                              [0x3f, 0xff, < 0x10, ..] => "documentation range (3fff::/20)",
                              _ => null,
                            };

    return documentation is not null && !allowDocumentation ? documentation : null;
  }
}
