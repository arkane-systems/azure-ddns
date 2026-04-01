#region header

// AzureDdns.FunctionApp - IpResolver.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-03-30 7:41 PM

#endregion

#region using

using System.Net;
using System.Net.Sockets;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

#endregion

namespace AzureDdns.FunctionApp.Services;

public interface IIpResolver
{
  /// <summary>
  ///   Resolves the effective IP address for the DNS update operation.
  /// </summary>
  /// <param name="request">Incoming HTTP request.</param>
  /// <param name="explicitIp">Optional caller-supplied IP query value.</param>
  /// <returns>
  ///   Resolution result containing effective IP, observed source IP, and mismatch indicator.
  /// </returns>
  IpResolutionResult Resolve (HttpRequest request, string? explicitIp);
}

/// <summary>
///   Resolves update IPs from caller input while preserving source-IP visibility.
/// </summary>
public sealed class IpResolver : IIpResolver
{
  private const string ForwardedForHeaderName = "X-Forwarded-For";

  /// <summary>
  ///   Determines which IP address should be written to DNS.
  /// </summary>
  /// <remarks>
  ///   If <paramref name="explicitIp" /> is supplied and valid, it is used as the effective IP.
  ///   When both explicit and source IP exist but differ, mismatch is flagged for auditing/logging.
  /// </remarks>
  public IpResolutionResult Resolve (HttpRequest request, string? explicitIp)
  {
    ArgumentNullException.ThrowIfNull (request);

    IPAddress? sourceIp = GetSourceIp (request);

    if (string.IsNullOrWhiteSpace (explicitIp))
      return new IpResolutionResult (EffectiveIp: sourceIp, SourceIp: sourceIp, ExplicitIpMismatch: false);

    if (!IPAddress.TryParse (ipString: explicitIp, address: out IPAddress? parsedExplicitIp))
      return new IpResolutionResult (EffectiveIp: null, SourceIp: sourceIp, ExplicitIpMismatch: false);

    bool mismatch = sourceIp is not null && !sourceIp.Equals (parsedExplicitIp);

    return new IpResolutionResult (EffectiveIp: parsedExplicitIp, SourceIp: sourceIp, ExplicitIpMismatch: mismatch);
  }

  /// <summary>
  ///   Prefers the first forwarded client IP only when the immediate caller looks like a trusted proxy hop.
  /// </summary>
  private static IPAddress? GetSourceIp (HttpRequest request)
  {
    IPAddress? remoteIp = request.HttpContext.Connection.RemoteIpAddress;

    return !IsTrustedProxyHop (remoteIp) ? remoteIp : TryGetForwardedForIp (request) ?? remoteIp;
  }

  private static IPAddress? TryGetForwardedForIp (HttpRequest request)
  {
    if (!request.Headers.TryGetValue (key: ForwardedForHeaderName, value: out StringValues forwardedForValues))
      return null;

    foreach (string? headerValue in forwardedForValues)
    {
      string[] entries = headerValue!.Split (separator: ',',
                                             options: StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

      foreach (string entry in entries)
      {
        if (TryParseForwardedForEntry (entry: entry, ipAddress: out IPAddress? parsedAddress))
          return parsedAddress;
      }
    }

    return null;
  }

  private static bool TryParseForwardedForEntry (string entry, out IPAddress? ipAddress)
  {
    string candidate = entry.Trim ();

    if (candidate.Length == 0)
    {
      ipAddress = null;

      return false;
    }

    if (candidate[0] == '[')
    {
      int endBracketIndex = candidate.IndexOf (']');

      if (endBracketIndex > 1)
        candidate = candidate[1..endBracketIndex];
    }
    else if (GetCharacterCount (value: candidate, character: ':') == 1)
    {
      int separatorIndex = candidate.LastIndexOf (':');

      if (separatorIndex > 0)
        candidate = candidate[..separatorIndex];
    }

    bool parsed = IPAddress.TryParse (ipString: candidate, address: out IPAddress? parsedAddress);
    ipAddress = parsed ? parsedAddress : null;

    return parsed;
  }

  private static bool IsTrustedProxyHop (IPAddress? address)
  {
    if (address is null)
      return false;

    if (IPAddress.IsLoopback (address))
      return true;

    if (address.AddressFamily == AddressFamily.InterNetworkV6)
    {
      if (address.IsIPv4MappedToIPv6)
        address = address.MapToIPv4 ();
      else
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || IsUniqueLocalIpv6 (address);
    }

    byte[] bytes = address.GetAddressBytes ();

    return (bytes[0] == 10)                                   ||
           ((bytes[0] == 172) && bytes[1] is >= 16 and <= 31) ||
           ((bytes[0] == 192) && (bytes[1] == 168))           ||
           ((bytes[0] == 169) && (bytes[1] == 254));
  }

  private static bool IsUniqueLocalIpv6 (IPAddress address) => (address.GetAddressBytes ()[0] & 0xfe) == 0xfc;

  private static int GetCharacterCount (string value, char character)
  {
    var count = 0;

    foreach (char current in value)
    {
      if (current == character)
        count++;
    }

    return count;
  }
}

/// <summary>
///   Captures resolved IP details for DDNS update and diagnostics.
/// </summary>
/// <param name="EffectiveIp">IP address selected for DNS update; <see langword="null" /> when resolution fails.</param>
/// <param name="SourceIp">Remote source IP from the incoming request context.</param>
/// <param name="ExplicitIpMismatch">Indicates caller-supplied IP differs from request source IP.</param>
public sealed record IpResolutionResult (IPAddress? EffectiveIp, IPAddress? SourceIp, bool ExplicitIpMismatch);
