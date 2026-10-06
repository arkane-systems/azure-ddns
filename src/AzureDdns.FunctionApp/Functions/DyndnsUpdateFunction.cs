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

using AzureDdns.FunctionApp.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Primitives;

#endregion

namespace AzureDdns.FunctionApp.Functions;

/// <summary>
///   HTTP front end for clients that speak the DynDNS v2 protocol, such as the Unifi Express 7 Cloud
///   Gateway, ddclient and OpenWRT's <c>dyndns2</c> provider.
/// </summary>
/// <remarks>
///   <para>
///     This class only translates between the DynDNS v2 wire format and the protocol-neutral
///     <see cref="IDdnsUpdateCoordinator" />, which holds all of the actual policy (configuration,
///     authentication, authorization, IP resolution and the DNS write). Another API can be added later
///     by writing another thin front end like this one.
///   </para>
///   <para>
///     Wire format:
///     <list type="bullet">
///       <item>
///         Authentication is via HTTP Basic Auth (<c>Authorization: Basic</c> header).
///         Username = client name; password = raw key — these map directly to the client/key
///         configuration in <c>config/dyndns.json</c>.
///       </item>
///       <item>
///         The target record is identified by a fully-qualified hostname (<c>hostname</c>, for example
///         <c>home.example.com</c>); the coordinator splits it into zone + record name by matching it
///         against the zones declared in the configuration file.
///       </item>
///       <item>
///         The optional <c>myip</c> parameter carries the address (IPv4 or IPv6); its family decides
///         whether an <c>A</c> or <c>AAAA</c> record is written. When omitted, the request's source IP is used.
///       </item>
///     </list>
///   </para>
///   <para>
///     Responses follow the DynDNS v2 response code convention (plain text body):
///     <c>good&lt;space&gt;&lt;ip&gt;</c> on success, <c>badauth</c> (HTTP 401) for authentication failure,
///     <c>nohost</c> when the hostname is not configured or the client is not authorised, and <c>911</c>
///     for a server-side error (HTTP 503 when the configuration file itself is unavailable, otherwise 200).
///   </para>
/// </remarks>
public sealed class DyndnsUpdateFunction (IDdnsUpdateCoordinator coordinator)
{
  private readonly IDdnsUpdateCoordinator coordinator = coordinator;

  /// <summary>
  ///   Processes a DynDNS v2 update request.
  /// </summary>
  /// <remarks>
  ///   Credentials are parsed first so a request with no usable <c>Authorization</c> header is rejected
  ///   with <c>badauth</c> before any configuration is read. Everything after that is the coordinator's job.
  /// </remarks>
  [Function ("DyndnsUpdate")]
  public async Task<IActionResult> RunAsync (

    // ReSharper disable once BadParensLineBreaks
    [HttpTrigger (authLevel: AuthorizationLevel.Anonymous, "get", Route = "nic/update")]
    HttpRequest request,
    CancellationToken cancellationToken)
  {
    if (!TryParseBasicAuth (request: request, clientName: out string? clientName, rawKey: out string? rawKey))
      return Badauth ();

    // clientName and rawKey are non-null here: TryParseBasicAuth only returns true when both were parsed.
    var updateRequest = new DdnsUpdateRequest (ClientName: clientName!,
                                               RawKey: rawKey!,
                                               Hostname: GetQueryValue (request: request, key: "hostname"),
                                               ExplicitIp: GetQueryValue (request: request, key: "myip"),
                                               HttpRequest: request);

    DdnsUpdateResult result = await this.coordinator.UpdateAsync (request: updateRequest, cancellationToken: cancellationToken);

    return result.Status switch
           {
             DdnsUpdateStatus.Success => Good (result.Update!.IpAddress),
             DdnsUpdateStatus.InvalidCredentials => Badauth (),

             // The DynDNS protocol has no "forbidden" code, and we must not reveal whether a host exists,
             // so an unknown host and a host the client may not update look identical.
             DdnsUpdateStatus.UnknownHost or DdnsUpdateStatus.RecordNotAuthorized => Nohost (),

             DdnsUpdateStatus.ConfigurationUnavailable => ServiceUnavailable (),
             _ => ServerError (),
           };
  }

  /// <summary>
  ///   Extracts the client name and raw key from an HTTP Basic <c>Authorization</c> header.
  /// </summary>
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
    int    colonIndex  = credentials.IndexOf (':');

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

  private static string? GetQueryValue (HttpRequest request, string key)
  {
    string value = request.Query[key].ToString ();

    return string.IsNullOrWhiteSpace (value) ? null : value.Trim ();
  }

  // ── DynDNS v2 response helpers ────────────────────────────────────────────────────────────

  private static ContentResult Good (string ip)
    => new () { Content = $"good {ip}", ContentType = "text/plain", StatusCode = StatusCodes.Status200OK, };

  private static ContentResult Badauth ()
    => new ()
       { Content = "badauth", ContentType = "text/plain", StatusCode = StatusCodes.Status401Unauthorized, };

  private static ContentResult Nohost ()
    => new () { Content = "nohost", ContentType = "text/plain", StatusCode = StatusCodes.Status200OK, };

  private static ContentResult ServiceUnavailable ()
    => new ()
       { Content = "911", ContentType = "text/plain", StatusCode = StatusCodes.Status503ServiceUnavailable, };

  private static ContentResult ServerError ()
    => new () { Content = "911", ContentType = "text/plain", StatusCode = StatusCodes.Status200OK, };
}
