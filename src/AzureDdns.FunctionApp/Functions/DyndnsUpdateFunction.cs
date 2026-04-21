#region header

// AzureDdns.FunctionApp - DyndnsUpdateFunction.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-04-18 12:00 AM

#endregion

#region using

using System.Text;

using Azure;

using AzureDdns.FunctionApp.Config;
using AzureDdns.FunctionApp.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

#endregion

namespace AzureDdns.FunctionApp.Functions;

/// <summary>
///   Handles dynamic DNS update requests from clients that speak the DynDNS v2 protocol,
///   such as the Unifi Express 7 Cloud Gateway.
/// </summary>
/// <remarks>
///   <para>
///     The DynDNS v2 protocol differs from the custom <c>/api/update</c> endpoint in two ways:
///     <list type="bullet">
///       <item>
///         Authentication is via HTTP Basic Auth (<c>Authorization: Basic</c> header).
///         Username = client name; password = raw key — these map directly to the existing
///         client/key configuration in <c>config/dyndns.json</c>.
///       </item>
///       <item>
///         The target record is identified by a fully-qualified hostname (e.g.
///         <c>home.example.com</c>), not by separate zone and record parameters.
///         <see cref="IFqdnResolver" /> splits the FQDN into zone + record name by
///         matching against the zones declared in the configuration file.
///       </item>
///     </list>
///   </para>
///   <para>
///     Responses follow the DynDNS v2 response code convention (plain text body):
///     <c>good&lt;space&gt;&lt;ip&gt;</c> on success, <c>badauth</c> for authentication failure,
///     <c>nohost</c> when the hostname is not configured or the client is not authorised,
///     and <c>911</c> for a server-side error.
///   </para>
///   <para>
///     IPv4 and IPv6 updates share this single endpoint.  Unifi requires two separate DDNS
///     entries — one whose Server URL contains <c>{IP}</c> and one containing <c>{IP6}</c>.
///     The IP family of the <c>myip</c> parameter determines whether an <c>A</c> or
///     <c>AAAA</c> record is written, reusing the existing <see cref="IDnsUpdateService" />
///     dispatch logic.
///   </para>
/// </remarks>
public sealed class DyndnsUpdateFunction (
  IConfigProvider               configProvider,
  IAuthService                  authService,
  IFqdnResolver                 fqdnResolver,
  IIpResolver                   ipResolver,
  IDnsUpdateService             dnsUpdateService,
  ILogger<DyndnsUpdateFunction> logger)
{
  private readonly IAuthService                  authService      = authService;
  private readonly IConfigProvider               configProvider   = configProvider;
  private readonly IDnsUpdateService             dnsUpdateService = dnsUpdateService;
  private readonly IFqdnResolver                 fqdnResolver     = fqdnResolver;
  private readonly IIpResolver                   ipResolver       = ipResolver;
  private readonly ILogger<DyndnsUpdateFunction> logger           = logger;

  /// <summary>
  ///   Processes a DynDNS v2 update request and writes the matching <c>A</c> or <c>AAAA</c>
  ///   record to Azure DNS.
  /// </summary>
  /// <remarks>
  ///   Execution order is: resolve hostname -> authenticate -> authorise -> resolve IP -> update DNS.
  ///   This means an unknown or unmapped hostname can return <c>nohost</c> before credentials are
  ///   validated, which matches the current implementation and DynDNS response mapping used here.
  ///   Responses use the DynDNS v2 response codes rather than the custom <c>OK:</c>/<c>ERROR:</c>
  ///   convention so that Unifi and other DynDNS-aware clients can interpret results correctly.
  /// </remarks>
  [Function ("DyndnsUpdate")]
  public async Task<IActionResult> RunAsync (

    // ReSharper disable once BadParensLineBreaks
    [HttpTrigger (authLevel: AuthorizationLevel.Anonymous, "get", Route = "nic/update")]
    HttpRequest request,
    CancellationToken cancellationToken)
  {
    // Step 1: Extract credentials from the HTTP Basic Auth header.
    //         The DynDNS v2 protocol mandates Basic Auth; Unifi sends the Username and Password
    //         fields as the Authorization header rather than embedding them in the URL.
    if (!TryParseBasicAuth (request: request, clientName: out string? clientName, rawKey: out string? rawKey))
      return Badauth ();

    // Step 2: Extract and validate the hostname (FQDN) parameter.
    string? hostname = GetQueryValue (request: request, key: "hostname");

    if (hostname is null)
      return Nohost ();

    // Step 3: Load configuration and resolve the FQDN to a configured zone + record name.
    //         If no configured zone matches the hostname, the DynDNS "nohost" code is returned.
    DyndnsConfig config = await this.configProvider.GetConfigAsync (cancellationToken);
    FqdnResolution? resolution = this.fqdnResolver.Resolve (hostname: hostname, zones: config.Zones);

    if (resolution is null)
      return Nohost ();

    // Step 4: Authenticate the caller.
    //         clientName and rawKey are guaranteed non-null here: TryParseBasicAuth only returns
    //         true when both values have been successfully parsed from the Authorization header.
    ClientConfig? authenticatedClient = this.authService.Authenticate (clientName: clientName!,
                                                                        rawKey: rawKey!,
                                                                        config: config);

    if (authenticatedClient is null)
      return Badauth ();

    // Step 5: Authorise the client for the resolved zone/record pair.
    //         "nohost" is returned for both missing and forbidden records; the DynDNS protocol
    //         has no distinct forbidden code, and we should not reveal whether the host exists.
    if (!this.authService.IsRecordAuthorized (client: authenticatedClient,
                                              zone: resolution.Zone,
                                              name: resolution.Name))
      return Nohost ();

    // Step 6: Resolve the effective IP address.
    //         "myip" is the standard DynDNS v2 query parameter for the caller-supplied IP.
    //         When omitted, the source IP of the request is used as a fallback.
    string? explicitIp = GetQueryValue (request: request, key: "myip");
    IpResolutionResult ipResolution = this.ipResolver.Resolve (request: request, explicitIp: explicitIp);

    if (ipResolution.EffectiveIp is null)
    {
      this.logger.LogWarning (message: "DynDNS: unable to resolve effective IP for {Hostname}; "                                        +
                                       "source IP was {SourceIp}, explicit myip was {ExplicitIp}.",
                              hostname,
                              ipResolution.SourceIp,
                              explicitIp);

      return ServerError ();
    }

    // Step 7: Write the DNS record and return the appropriate DynDNS response code.
    //         FqdnResolver normalizes zones by trimming whitespace and a trailing dot.
    //         Configuration keys may retain their original formatting, so avoid direct
    //         dictionary indexing here and perform a normalization-aware lookup instead.
    static string NormalizeZoneKey (string zone)
    {
      return zone.Trim ().TrimEnd ('.');
    }

    ZoneConfig? zoneConfig = null;

    if (!config.Zones.TryGetValue (resolution.Zone, out zoneConfig))
    {
      string normalizedResolvedZone = NormalizeZoneKey (resolution.Zone);

      foreach ((string configuredZoneKey, ZoneConfig configuredZone) in config.Zones)
      {
        if (string.Equals (NormalizeZoneKey (configuredZoneKey),
                           normalizedResolvedZone,
                           StringComparison.OrdinalIgnoreCase))
        {
          zoneConfig = configuredZone;
          break;
        }
      }
    }

    if (zoneConfig is null)
    {
      this.logger.LogError (message: "DynDNS: resolved zone {Zone} did not match any configured zone key. " +
                                    "Check zone key normalization in configuration.",
                            resolution.Zone);

      return ServerError ();
    }
    try
    {
      UpdateDnsResult result = await this.dnsUpdateService.UpdateAsync (zone: resolution.Zone,
                                                                         name: resolution.Name,
                                                                         ipAddress: ipResolution.EffectiveIp,
                                                                         zoneConfig: zoneConfig,
                                                                         cancellationToken: cancellationToken);

      this.logger.LogInformation (message: "DynDNS updated {RecordType} record {Fqdn} for client {Client} to {IpAddress}.",
                                  result.RecordType,
                                  result.Fqdn,
                                  authenticatedClient.Name,
                                  result.IpAddress);

      return Good (result.IpAddress);
    }
    catch (Exception exception) when (exception is RequestFailedException or ArgumentException or InvalidOperationException)
    {
      this.logger.LogError (exception: exception,
                            message: "DynDNS DNS update failed for client {Client}, hostname {Hostname}.",
                            authenticatedClient.Name,
                            hostname);

      return ServerError ();
    }
  }

  /// <summary>
  ///   Extracts client name and raw key from an HTTP Basic Auth header.
  /// </summary>
  /// <remarks>
  ///   Per RFC 7617, credentials are Base64-encoded as <c>username:password</c>.  The password
  ///   may contain colons; only the first colon is treated as the delimiter.
  /// </remarks>
  private static bool TryParseBasicAuth (HttpRequest request, out string? clientName, out string? rawKey)
  {
    clientName = null;
    rawKey     = null;

    if (!request.Headers.TryGetValue (key: "Authorization", value: out StringValues authValues))
      return false;

    string? authHeader = authValues.ToString ();

    if (string.IsNullOrWhiteSpace (authHeader) ||
        !authHeader.StartsWith ("Basic ", StringComparison.OrdinalIgnoreCase))
      return false;

    string base64 = authHeader["Basic ".Length..].Trim ();

    byte[] bytes;

    try
    {
      bytes = Convert.FromBase64String (base64);
    }
    catch (FormatException)
    {
      return false;
    }

    string credentials = Encoding.UTF8.GetString (bytes);
    int    colonIndex   = credentials.IndexOf (':');

    if (colonIndex < 0)
      return false;

    string name = credentials[..colonIndex];
    string key  = credentials[(colonIndex + 1)..];

    if (string.IsNullOrWhiteSpace (name) || string.IsNullOrWhiteSpace (key))
      return false;

    clientName = name.Trim ();
    rawKey     = key; // Raw key is not trimmed — password content is treated verbatim.

    return true;
  }

  /// <summary>
  ///   Returns a trimmed query-string value or <see langword="null" /> when absent/blank.
  /// </summary>
  private static string? GetQueryValue (HttpRequest request, string key)
  {
    string value = request.Query[key].ToString ();

    return string.IsNullOrWhiteSpace (value) ? null : value.Trim ();
  }

  // ── DynDNS v2 response helpers ────────────────────────────────────────────────────────────

  /// <summary>
  ///   DynDNS v2 success response: <c>good &lt;ip&gt;</c>.  The IP address is echoed back
  ///   so the client can confirm what was registered.
  /// </summary>
  private static ContentResult Good (string ip)
    => new () { Content = $"good {ip}", ContentType = "text/plain", StatusCode = StatusCodes.Status200OK, };

  /// <summary>
  ///   DynDNS v2 authentication failure response: <c>badauth</c> (HTTP 401).
  /// </summary>
  private static ContentResult Badauth ()
    => new ()
       { Content = "badauth", ContentType = "text/plain", StatusCode = StatusCodes.Status401Unauthorized, };

  /// <summary>
  ///   DynDNS v2 hostname-not-found response: <c>nohost</c> (HTTP 200).
  ///   Also used for authorisation failures to avoid revealing whether a hostname is configured.
  /// </summary>
  private static ContentResult Nohost ()
    => new () { Content = "nohost", ContentType = "text/plain", StatusCode = StatusCodes.Status200OK, };

  /// <summary>
  ///   DynDNS v2 server-error response: <c>911</c> (HTTP 200).
  ///   Clients that respect the protocol should back off and retry after a delay.
  /// </summary>
  private static ContentResult ServerError ()
    => new () { Content = "911", ContentType = "text/plain", StatusCode = StatusCodes.Status200OK, };
}
