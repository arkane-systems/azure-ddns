#region header

// AzureDdns.FunctionApp - IpDiagnosticsLog.cs
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
using Microsoft.Extensions.Logging;

#endregion

namespace AzureDdns.FunctionApp.Services;

/// <summary>
///   Shared logging for source-IP diagnostics, used by both HTTP functions so that they behave identically.
/// </summary>
/// <remarks>
///   <para>
///     Every request logs a one-line summary of how the source IP was derived (peer address, trusted-proxy
///     decision, parsed and raw forwarding headers). When the
///     <c>LOG_ALL_REQUEST_HEADERS_FOR_IP_DIAGNOSTICS</c> setting is enabled, all request headers are logged as
///     well, with credential-bearing headers redacted.
///   </para>
///   <para>
///     Never log the query string here: <c>/api/update</c> carries the raw client key in it.
///   </para>
///   <para>
///     Everything logged that originated from the request (headers, hostnames, client names) is passed through
///     <see cref="Sanitize" /> first, so a caller cannot forge log lines by embedding line breaks or other control
///     characters in a header or parameter (log forging, CWE-117).
///   </para>
/// </remarks>
public static class IpDiagnosticsLog
{
  private const string Redacted = "<redacted>";

  /// <summary>
  ///   A header is treated as credential-bearing, and its value redacted, when its name contains any of these
  ///   fragments (case-insensitive). Matching on fragments rather than an exact list also covers headers we have
  ///   not anticipated, such as <c>Proxy-Authorization</c>, <c>X-Api-Key</c> or <c>x-ms-token-aad-id-token</c>.
  /// </summary>
  private static readonly string[] SensitiveHeaderFragments = ["auth", "cookie", "key", "token", "secret", "password", "credential",];

  /// <summary>
  ///   Makes a request-derived string safe to write to a log: control characters (including CR and LF) are
  ///   replaced with an underscore so the value cannot start a new, forged log line.
  /// </summary>
  /// <param name="value">The value to sanitize; <see langword="null" /> is returned unchanged.</param>
  public static string? Sanitize (string? value)
    => value is null ? null : string.Create (length: value.Length,
                                             state: value,
                                             action: static (span, source) =>
                                                     {
                                                       for (var index = 0; index < source.Length; index++)
                                                         span[index] = char.IsControl (source[index]) ? '_' : source[index];
                                                     });

  private static bool IsSensitiveHeader (string headerName)
    => SensitiveHeaderFragments.Any (fragment => headerName.Contains (value: fragment, comparisonType: StringComparison.OrdinalIgnoreCase));

  /// <summary>
  ///   Logs the IP resolution summary, optionally all request headers, and a warning when the source IP
  ///   resolved to loopback (usually a sign that reverse-proxy header forwarding is misconfigured).
  /// </summary>
  /// <param name="logger">Destination logger.</param>
  /// <param name="target">Human-readable record being updated (for example <c>home.example.com</c>).</param>
  /// <param name="request">The incoming request (for the optional full header dump).</param>
  /// <param name="resolution">The result of <see cref="IIpResolver.Resolve" />.</param>
  /// <param name="logAllHeaders">Whether to log every request header (redacting sensitive ones).</param>
  public static void LogResolution (ILogger             logger,
                                    string              target,
                                    HttpRequest         request,
                                    IpResolutionResult  resolution,
                                    bool                logAllHeaders)
  {
    ArgumentNullException.ThrowIfNull (logger);
    ArgumentNullException.ThrowIfNull (request);
    ArgumentNullException.ThrowIfNull (resolution);

    IpResolutionDiagnostics diagnostics = resolution.Diagnostics;

    logger.LogInformation (message:
                           "IP resolution diagnostics for {Target}: remote={RemoteIp}, source={SourceIp}, trustedProxyHop={TrustedProxyHop}, parsedForwardedFor={ParsedForwardedFor}, parsedClientIp={ParsedClientIp}, xForwardedFor={XForwardedFor}, forwarded={Forwarded}, xOriginalFor={XOriginalFor}, xRealIp={XRealIp}, clientIp={ClientIp}.",
                           Sanitize (target),
                           diagnostics.RemoteIp,
                           resolution.SourceIp,
                           diagnostics.TrustedProxyHop,
                           diagnostics.ForwardedForIp,
                           diagnostics.ClientIp,
                           Sanitize (diagnostics.ForwardedForHeader),
                           Sanitize (diagnostics.ForwardedHeader),
                           Sanitize (diagnostics.XOriginalForHeader),
                           Sanitize (diagnostics.XRealIpHeader),
                           Sanitize (diagnostics.ClientIpHeader));

    if (logAllHeaders)
    {
      Dictionary<string, string> headers = request.Headers.ToDictionary (keySelector: pair => Sanitize (pair.Key)!,
                                                                         elementSelector: pair => IsSensitiveHeader (pair.Key)
                                                                                                    ? Redacted
                                                                                                    : Sanitize (pair.Value.ToString ())!,
                                                                         comparer: StringComparer.OrdinalIgnoreCase);

      logger.LogInformation (message: "Full request header diagnostics for {Target}: {@Headers}", Sanitize (target), headers);
    }

    if (resolution.SourceIp is not null && IPAddress.IsLoopback (resolution.SourceIp))
      logger.LogWarning (message:
                         "Source IP resolved to loopback for {Target}; confirm reverse-proxy header forwarding configuration.",
                         Sanitize (target));
  }

  /// <summary>
  ///   Logs a warning when a client-supplied explicit IP differs from the resolved source IP. The update
  ///   still proceeds; the mismatch is informational (for example a client behind NAT updating a tunnel address).
  /// </summary>
  public static void LogExplicitIpMismatch (ILogger logger, string client, string target, IpResolutionResult resolution)
  {
    ArgumentNullException.ThrowIfNull (logger);
    ArgumentNullException.ThrowIfNull (resolution);

    if (!resolution.ExplicitIpMismatch)
      return;

    logger.LogWarning (message: "Client {Client} supplied explicit IP {ExplicitIp} differing from source IP {SourceIp} for {Target}.",
                       Sanitize (client),
                       resolution.EffectiveIp,
                       resolution.SourceIp,
                       Sanitize (target));
  }
}
