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
    public UpdateDnsFunction (IConfigProvider configProvider,
                              IAuthService authService,
                              IIpResolver ipResolver,
                              IDnsUpdateService dnsUpdateService,
                              ILogger<UpdateDnsFunction> logger)
    {
        this._configProvider = configProvider;
        this._authService = authService;
        this._ipResolver = ipResolver;
        this._dnsUpdateService = dnsUpdateService;
        this._logger = logger;
    }

    private readonly IAuthService                _authService ;
    private readonly IConfigProvider             _configProvider ;
    private readonly IDnsUpdateService           _dnsUpdateService ;
    private readonly IIpResolver                 _ipResolver ;
    private readonly ILogger <UpdateDnsFunction> _logger ;

    /// <summary>
    ///     Handles DDNS update requests and upserts matching Azure DNS A or AAAA records.
    /// </summary>
    [Function ("UpdateDns")]
    public async Task<IActionResult> RunAsync (
        [HttpTrigger (authLevel: AuthorizationLevel.Anonymous, "get", Route = "update")] HttpRequest request,
        CancellationToken cancellationToken)
    {
        string? client     = GetQueryValue (request: request, key: "client") ;
        string? key        = GetQueryValue (request: request, key: "key") ;
        string? zone       = GetQueryValue (request: request, key: "zone") ;
        string? name       = GetQueryValue (request: request, key: "name") ;
        string? explicitIp = GetQueryValue (request: request, key: "ip") ;

        if (client is null)
            return Error (statusCode: StatusCodes.Status400BadRequest, message: "missing client");

        if (key is null)
            return Error (statusCode: StatusCodes.Status400BadRequest, message: "missing key");

        if (zone is null)
            return Error (statusCode: StatusCodes.Status400BadRequest, message: "missing zone");

        if (name is null)
            return Error (statusCode: StatusCodes.Status400BadRequest, message: "missing name");

        DyndnsConfig config = await this._configProvider.GetConfigAsync (cancellationToken) ;
        ZoneConfig? zoneConfig =
            config.Zones.TryGetValue (key: zone, value: out ZoneConfig? configuredZone) ? configuredZone : null ;

        if (zoneConfig is null)
            return Error (statusCode: StatusCodes.Status400BadRequest, message: "zone not configured");

        ClientConfig? authenticatedClient = this._authService.Authenticate (clientName: client, rawKey: key, config: config) ;

        if (authenticatedClient is null)
            return Error (statusCode: StatusCodes.Status401Unauthorized, message: "invalid credentials");

        if (!this._authService.IsRecordAuthorized (client: authenticatedClient, zone: zone, name: name))
            return Error (statusCode: StatusCodes.Status403Forbidden, message: "unauthorized record");

        IpResolutionResult resolution = this._ipResolver.Resolve (request: request, explicitIp: explicitIp) ;

        if (resolution.EffectiveIp is null)
            return Error (statusCode: StatusCodes.Status400BadRequest,
                          message: explicitIp is null ? "unable to resolve source IP" : "invalid IP address");

        if (resolution.ExplicitIpMismatch)
            this._logger.LogWarning (message:
                                     "Client {Client} supplied explicit IP {ExplicitIp} differing from source IP {SourceIp} for {Record}.{Zone}.",
                                     authenticatedClient.Name,
                                     resolution.EffectiveIp,
                                     resolution.SourceIp,
                                     name,
                                     zone);

        try
        {
            UpdateDnsResult result = await this._dnsUpdateService.UpdateAsync (zone: zone,
                                                                               name: name,
                                                                               ipAddress: resolution.EffectiveIp,
                                                                               zoneConfig: zoneConfig,
                                                                               cancellationToken: cancellationToken) ;
            this._logger.LogInformation (message: "Updated {RecordType} record {Fqdn} for client {Client} to {IpAddress}.",
                                         result.RecordType,
                                         result.Fqdn,
                                         authenticatedClient.Name,
                                         result.IpAddress);

            return Success ($"updated {result.RecordType} {result.Fqdn} to {result.IpAddress}");
        }
        catch (ArgumentException exception)
        {
            this._logger.LogWarning (exception: exception,
                                     message: "Invalid DNS update request for client {Client}, zone {Zone}, record {Record}.",
                                     authenticatedClient.Name,
                                     zone,
                                     name);

            return Error (statusCode: StatusCodes.Status400BadRequest, message: "invalid request");
        }
        catch (RequestFailedException exception)
        {
            this._logger.LogError (exception: exception,
                                   message: "Azure DNS update failed for client {Client}, zone {Zone}, record {Record}.",
                                   authenticatedClient.Name,
                                   zone,
                                   name);

            return Error (statusCode: StatusCodes.Status502BadGateway, message: "dns update failed");
        }
        catch (InvalidOperationException exception)
        {
            this._logger.LogError (exception: exception,
                                   message: "Function configuration is invalid for client {Client}, zone {Zone}, record {Record}.",
                                   authenticatedClient.Name,
                                   zone,
                                   name);

            return Error (statusCode: StatusCodes.Status500InternalServerError, message: "server configuration invalid");
        }
    }

    private static string? GetQueryValue (HttpRequest request, string key)
    {
        var value = request.Query[key].ToString () ;

        return string.IsNullOrWhiteSpace (value) ? null : value.Trim ();
    }

    private static ContentResult Success (string message)
        => new () { Content = $"OK: {message}", ContentType = "text/plain", StatusCode = StatusCodes.Status200OK, };

    private static ContentResult Error (int statusCode, string message)
        => new () { Content = $"ERROR: {message}", ContentType = "text/plain", StatusCode = statusCode, };
}
