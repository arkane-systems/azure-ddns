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

using Microsoft.AspNetCore.Http;

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
  /// <summary>
  ///   Determines which IP address should be written to DNS.
  /// </summary>
  /// <remarks>
  ///   If <paramref name="explicitIp" /> is supplied and valid, it is used as the effective IP.
  ///   When both explicit and source IP exist but differ, mismatch is flagged for auditing/logging.
  /// </remarks>
  public IpResolutionResult Resolve (HttpRequest request, string? explicitIp)
  {
    IPAddress? sourceIp = request.HttpContext.Connection.RemoteIpAddress;

    if (string.IsNullOrWhiteSpace (explicitIp))
      return new IpResolutionResult (EffectiveIp: sourceIp, SourceIp: sourceIp, ExplicitIpMismatch: false);

    if (!IPAddress.TryParse (ipString: explicitIp, address: out IPAddress? parsedExplicitIp))
      return new IpResolutionResult (EffectiveIp: null, SourceIp: sourceIp, ExplicitIpMismatch: false);

    bool mismatch = sourceIp is not null && !sourceIp.Equals (parsedExplicitIp);

    return new IpResolutionResult (EffectiveIp: parsedExplicitIp, SourceIp: sourceIp, ExplicitIpMismatch: mismatch);
  }
}

/// <summary>
///   Captures resolved IP details for DDNS update and diagnostics.
/// </summary>
/// <param name="EffectiveIp">IP address selected for DNS update; <see langword="null" /> when resolution fails.</param>
/// <param name="SourceIp">Remote source IP from the incoming request context.</param>
/// <param name="ExplicitIpMismatch">Indicates caller-supplied IP differs from request source IP.</param>
public sealed record IpResolutionResult (IPAddress? EffectiveIp, IPAddress? SourceIp, bool ExplicitIpMismatch);
