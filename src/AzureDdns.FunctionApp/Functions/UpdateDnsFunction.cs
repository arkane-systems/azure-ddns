#region header

// AzureDdns.FunctionApp - UpdateDnsFunction.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-03-30 7:41 PM

#endregion

#region using

using Azure;

using AzureDdns.FunctionApp.Config;
using AzureDdns.FunctionApp.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

#endregion

namespace AzureDdns.FunctionApp.Functions;

public sealed class UpdateDnsFunction
{
  /// <summary>
  ///   Coordinates dynamic DNS update requests end-to-end.
  /// </summary>
  /// <remarks>
  ///   The function intentionally keeps transport concerns (HTTP query parsing and response formatting)
  ///   in this class while delegating authentication, IP resolution, and DNS operations to services.
  ///   This keeps behavior testable and makes it easier to evolve service logic independently.
  /// </remarks>
  public UpdateDnsFunction (IConfigProvider            configProvider,
                            IAuthService               authService,
                            IIpResolver                ipResolver,
                            IDnsUpdateService          dnsUpdateService,
                            ILogger<UpdateDnsFunction> logger)
  {
    this.configProvider   = configProvider;
    this.authService      = authService;
    this.ipResolver       = ipResolver;
    this.dnsUpdateService = dnsUpdateService;
    this.logger           = logger;
  }

  private readonly IAuthService               authService;
  private readonly IConfigProvider            configProvider;
  private readonly IDnsUpdateService          dnsUpdateService;
  private readonly IIpResolver                ipResolver;
  private readonly ILogger<UpdateDnsFunction> logger;

  /// <summary>
  ///   Handles a DDNS update request and writes the matching <c>A</c> or <c>AAAA</c> record to Azure DNS.
  /// </summary>
  /// <remarks>
  ///   Execution order is intentionally strict:
  ///   validate request -> load config -> authenticate -> authorize -> resolve IP -> update DNS.
  ///   This avoids unnecessary Azure calls for invalid or unauthorized requests and keeps failure
  ///   responses deterministic for DDNS clients.
  /// </remarks>
  [Function ("UpdateDns")]
  public async Task<IActionResult> RunAsync (

    // ReSharper disable once BadParensLineBreaks
    [HttpTrigger (authLevel: AuthorizationLevel.Anonymous, "get", Route = "update")]
    HttpRequest request,
    CancellationToken cancellationToken)
  {
    string? client     = GetQueryValue (request: request, key: "client");
    string? key        = GetQueryValue (request: request, key: "key");
    string? zone       = GetQueryValue (request: request, key: "zone");
    string? name       = GetQueryValue (request: request, key: "name");
    string? explicitIp = GetQueryValue (request: request, key: "ip");

    if (client is null)
      return Error (statusCode: StatusCodes.Status400BadRequest, message: "missing client");

    if (key is null)
      return Error (statusCode: StatusCodes.Status400BadRequest, message: "missing key");

    if (zone is null)
      return Error (statusCode: StatusCodes.Status400BadRequest, message: "missing zone");

    if (name is null)
      return Error (statusCode: StatusCodes.Status400BadRequest, message: "missing name");

    // Normalize zone: trim whitespace and remove a trailing dot to accept fully-qualified names.
    // This must be done before config lookup and auth so all three use the same canonical form.
    zone = zone.Trim ().TrimEnd ('.');

    DyndnsConfig config = await this.configProvider.GetConfigAsync (cancellationToken);
    ZoneConfig? zoneConfig =
      config.Zones.GetValueOrDefault (zone);

    if (zoneConfig is null)
      return Error (statusCode: StatusCodes.Status400BadRequest, message: "zone not configured");

    ClientConfig? authenticatedClient = this.authService.Authenticate (clientName: client, rawKey: key, config: config);

    if (authenticatedClient is null)
      return Error (statusCode: StatusCodes.Status401Unauthorized, message: "invalid credentials");

    if (!this.authService.IsRecordAuthorized (client: authenticatedClient, zone: zone, name: name))
      return Error (statusCode: StatusCodes.Status403Forbidden, message: "unauthorized record");

    IpResolutionResult resolution = this.ipResolver.Resolve (request: request, explicitIp: explicitIp);

    if (resolution.EffectiveIp is null)
      return Error (statusCode: StatusCodes.Status400BadRequest,
                    message: explicitIp is null ? "unable to resolve source IP" : "invalid IP address");

    if (resolution.ExplicitIpMismatch)
      this.logger.LogWarning (message:
                              "Client {Client} supplied explicit IP {ExplicitIp} differing from source IP {SourceIp} for {Record}.{Zone}.",
                              authenticatedClient.Name,
                              resolution.EffectiveIp,
                              resolution.SourceIp,
                              name,
                              zone);

    try
    {
      UpdateDnsResult result = await this.dnsUpdateService.UpdateAsync (zone: zone,
                                                                        name: name,
                                                                        ipAddress: resolution.EffectiveIp,
                                                                        zoneConfig: zoneConfig,
                                                                        cancellationToken: cancellationToken);
      this.logger.LogInformation (message: "Updated {RecordType} record {Fqdn} for client {Client} to {IpAddress}.",
                                  result.RecordType,
                                  result.Fqdn,
                                  authenticatedClient.Name,
                                  result.IpAddress);

      return Success ($"updated {result.RecordType} {result.Fqdn} to {result.IpAddress}");
    }
    catch (ArgumentException exception)
    {
      this.logger.LogWarning (exception: exception,
                              message: "Invalid DNS update request for client {Client}, zone {Zone}, record {Record}.",
                              authenticatedClient.Name,
                              zone,
                              name);

      return Error (statusCode: StatusCodes.Status400BadRequest, message: "invalid request");
    }
    catch (RequestFailedException exception)
    {
      this.logger.LogError (exception: exception,
                            message: "Azure DNS update failed for client {Client}, zone {Zone}, record {Record}.",
                            authenticatedClient.Name,
                            zone,
                            name);

      return Error (statusCode: StatusCodes.Status502BadGateway, message: "dns update failed");
    }
    catch (InvalidOperationException exception)
    {
      this.logger.LogError (exception: exception,
                            message: "Function configuration is invalid for client {Client}, zone {Zone}, record {Record}.",
                            authenticatedClient.Name,
                            zone,
                            name);

      return Error (statusCode: StatusCodes.Status500InternalServerError, message: "server configuration invalid");
    }
  }

  /// <summary>
  ///   Returns a trimmed query-string value or <see langword="null" /> when absent/blank.
  /// </summary>
  /// <remarks>
  ///   Normalizing early keeps downstream service logic focused on domain rules instead of input hygiene.
  /// </remarks>
  private static string? GetQueryValue (HttpRequest request, string key)
  {
    var value = request.Query[key].ToString ();

    return string.IsNullOrWhiteSpace (value) ? null : value.Trim ();
  }

  /// <summary>
  ///   Formats a successful DDNS response in plain text for broad client compatibility.
  /// </summary>
  private static ContentResult Success (string message)
    => new () { Content = $"OK: {message}", ContentType = "text/plain", StatusCode = StatusCodes.Status200OK, };

  /// <summary>
  ///   Formats an error DDNS response in plain text for broad client compatibility.
  /// </summary>
  private static ContentResult Error (int statusCode, string message)
    => new () { Content = $"ERROR: {message}", ContentType = "text/plain", StatusCode = statusCode, };
}
