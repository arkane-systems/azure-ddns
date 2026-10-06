#region header

// AzureDdns.FunctionApp - DdnsUpdateCoordinator.cs
//
// Alistair J. R. Young
// Arkane Systems
//
// Copyright Arkane Systems 2012-2018.  All rights reserved.
//
// Created: 2026-10-06 4:00 PM

#endregion

#region using

using Azure;
using Azure.Identity;

using AzureDdns.FunctionApp.Config;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#endregion

namespace AzureDdns.FunctionApp.Services;

/// <summary>
///   The outcome of a dynamic DNS update attempt, independent of any particular wire protocol.
/// </summary>
/// <remarks>
///   Each API front end (currently only the DynDNS v2 function) maps these onto its own response
///   convention. Outcomes are deliberately fine-grained; a front end that must not reveal the difference
///   between two of them (for example <see cref="UnknownHost" /> and <see cref="RecordNotAuthorized" />)
///   is responsible for collapsing them.
/// </remarks>
public enum DdnsUpdateStatus
{
  /// <summary>
  ///   The DNS record now holds the requested address. Check <c>Update.Changed</c> to tell whether it was written
  ///   or was already current.
  /// </summary>
  Success,

  /// <summary>The configuration file is missing, unreadable or malformed (a server fault).</summary>
  ConfigurationUnavailable,

  /// <summary>The client name/key pair did not authenticate.</summary>
  InvalidCredentials,

  /// <summary>No hostname was supplied, or it does not belong to any configured zone.</summary>
  UnknownHost,

  /// <summary>The client is authenticated but not allowed to update this zone/record.</summary>
  RecordNotAuthorized,

  /// <summary>The resolved zone has no configuration entry (a server-side configuration problem).</summary>
  ZoneNotConfigured,

  /// <summary>The effective IP address could not be determined or was not a usable address.</summary>
  InvalidAddress,

  /// <summary>
  ///   The effective address is valid but not one that belongs in public DNS (private, loopback, link-local,
  ///   multicast, ...). See <see cref="AddressPolicy" />.
  /// </summary>
  AddressNotPubliclyRoutable,

  /// <summary>Azure DNS (or the credential used to reach it) rejected or failed the write.</summary>
  DnsUpdateFailed,

  /// <summary>Required runtime settings (subscription, resource group) are missing.</summary>
  ServerMisconfigured,
}

/// <summary>
///   A protocol-neutral request to update one DNS record.
/// </summary>
/// <param name="ClientName">Client identifier presented by the caller.</param>
/// <param name="RawKey">Raw (unhashed) client key presented by the caller. Never log this.</param>
/// <param name="Hostname">Fully-qualified hostname to update, or <see langword="null" /> if the caller sent none.</param>
/// <param name="ExplicitIp">Caller-supplied IP address text, or <see langword="null" /> to use the source IP.</param>
/// <param name="HttpRequest">
///   The originating HTTP request, used only to derive the source IP when no explicit address was given.
/// </param>
public sealed record DdnsUpdateRequest (
  string      ClientName,
  string      RawKey,
  string?     Hostname,
  string?     ExplicitIp,
  HttpRequest HttpRequest);

/// <summary>
///   Result of <see cref="IDdnsUpdateCoordinator.UpdateAsync" />.
/// </summary>
/// <param name="Status">What happened.</param>
/// <param name="Update">Details of the record written; set only when <paramref name="Status" /> is success.</param>
public sealed record DdnsUpdateResult (DdnsUpdateStatus Status, UpdateDnsResult? Update = null)
{
  /// <summary>Whether the record was written.</summary>
  public bool IsSuccess => this.Status == DdnsUpdateStatus.Success;
}

/// <summary>
///   Performs a dynamic DNS update on behalf of an API front end: configuration, authentication,
///   authorization, hostname-to-zone resolution, IP resolution and the Azure DNS write.
/// </summary>
/// <remarks>
///   Front ends (HTTP functions) are responsible only for parsing their wire format into a
///   <see cref="DdnsUpdateRequest" /> and rendering a <see cref="DdnsUpdateResult" /> back out. All policy
///   lives here, so adding another API later does not duplicate it.
/// </remarks>
public interface IDdnsUpdateCoordinator
{
  /// <summary>
  ///   Runs the update pipeline. Never throws for expected failures; those are reported through the result.
  /// </summary>
  Task<DdnsUpdateResult> UpdateAsync (DdnsUpdateRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDdnsUpdateCoordinator" />
/// <remarks>
///   Order of checks: load config -> authenticate -> resolve hostname -> authorize -> resolve IP ->
///   check the address is publishable -> find zone settings -> write DNS. Authentication comes before hostname resolution so that an
///   unauthenticated caller learns nothing about which hostnames or zones are configured.
/// </remarks>
public sealed class DdnsUpdateCoordinator (
  IConfigProvider                configProvider,
  IAuthService                   authService,
  IFqdnResolver                  fqdnResolver,
  IIpResolver                    ipResolver,
  IDnsUpdateService              dnsUpdateService,
  IOptions<RuntimeSettings>      runtimeSettings,
  ILogger<DdnsUpdateCoordinator> logger) : IDdnsUpdateCoordinator
{
  private readonly IAuthService                   authService      = authService;
  private readonly IConfigProvider                configProvider   = configProvider;
  private readonly IDnsUpdateService              dnsUpdateService = dnsUpdateService;
  private readonly IFqdnResolver                  fqdnResolver     = fqdnResolver;
  private readonly IIpResolver                    ipResolver       = ipResolver;
  private readonly ILogger<DdnsUpdateCoordinator> logger           = logger;
  private readonly RuntimeSettings                runtimeSettings  = runtimeSettings.Value;

  /// <inheritdoc />
  public async Task<DdnsUpdateResult> UpdateAsync (DdnsUpdateRequest request, CancellationToken cancellationToken = default)
  {
    ArgumentNullException.ThrowIfNull (request);

    // Step 1: load configuration. A missing or malformed file is a server fault, not a bad request.
    DyndnsConfig config;

    try
    {
      config = await this.configProvider.GetConfigAsync (cancellationToken);
    }
    catch (ConfigurationUnavailableException exception)
    {
      this.logger.LogError (exception: exception, message: "DDNS configuration is unavailable.");

      return new DdnsUpdateResult (DdnsUpdateStatus.ConfigurationUnavailable);
    }

    // Step 2: authenticate.
    ClientConfig? client = this.authService.Authenticate (clientName: request.ClientName,
                                                          rawKey: request.RawKey,
                                                          config: config);

    if (client is null)
      return new DdnsUpdateResult (DdnsUpdateStatus.InvalidCredentials);

    // Step 3: resolve the hostname to a configured zone and relative record name.
    if (string.IsNullOrWhiteSpace (request.Hostname))
      return new DdnsUpdateResult (DdnsUpdateStatus.UnknownHost);

    string hostname = request.Hostname;

    FqdnResolution? resolution = this.fqdnResolver.Resolve (hostname: hostname, zones: config.Zones);

    if (resolution is null)
      return new DdnsUpdateResult (DdnsUpdateStatus.UnknownHost);

    // Step 4: authorize the client for the resolved zone/record.
    if (!this.authService.IsRecordAuthorized (client: client, zone: resolution.Zone, name: resolution.Name))
      return new DdnsUpdateResult (DdnsUpdateStatus.RecordNotAuthorized);

    // Step 5: resolve the effective IP address (explicit value, else the request's source IP).
    IpResolutionResult ipResolution = this.ipResolver.Resolve (request: request.HttpRequest, explicitIp: request.ExplicitIp);

    IpDiagnosticsLog.LogResolution (logger: this.logger,
                                    target: hostname,
                                    request: request.HttpRequest,
                                    resolution: ipResolution,
                                    logAllHeaders: this.runtimeSettings.LogAllRequestHeadersForIpDiagnostics);

    if (ipResolution.EffectiveIp is null)
    {
      this.logger.LogWarning (message: "Unable to resolve effective IP for {Hostname}; source IP was {SourceIp}, explicit IP was {ExplicitIp}.",
                              LogSanitizer.Sanitize (hostname),
                              ipResolution.SourceIp,
                              LogSanitizer.Sanitize (request.ExplicitIp));

      return new DdnsUpdateResult (DdnsUpdateStatus.InvalidAddress);
    }

    // Refuse to publish an address that cannot work in public DNS. This catches both a client reporting a LAN
    // address and the fallback case where only an internal proxy hop was visible as the source.
    string? rejection = AddressPolicy.RejectionReason (ipResolution.EffectiveIp);

    if (rejection is not null)
    {
      this.logger.LogWarning (message: "Refusing to publish {IpAddress} for {Hostname} (client {Client}): {Reason}. Source IP was {SourceIp}; explicit IP was {ExplicitIp}.",
                              ipResolution.EffectiveIp,
                              LogSanitizer.Sanitize (hostname),
                              LogSanitizer.Sanitize (client.Name),
                              rejection,
                              ipResolution.SourceIp,
                              LogSanitizer.Sanitize (request.ExplicitIp));

      return new DdnsUpdateResult (DdnsUpdateStatus.AddressNotPubliclyRoutable);
    }

    IpDiagnosticsLog.LogExplicitIpMismatch (logger: this.logger,
                                            client: client.Name,
                                            target: hostname,
                                            resolution: ipResolution);

    // Step 6: find the zone's settings (TTL). FqdnResolver only returns configured zones, so a miss here
    //         means the configured zone key could not be matched after normalization.
    if (!config.TryGetZone (zone: resolution.Zone, zoneConfig: out ZoneConfig? zoneConfig) || zoneConfig is null)
    {
      this.logger.LogError (message: "Resolved zone {Zone} did not match any configured zone key. Check zone key normalization in configuration.",
                            LogSanitizer.Sanitize (resolution.Zone));

      return new DdnsUpdateResult (DdnsUpdateStatus.ZoneNotConfigured);
    }

    // Step 7: write the record.
    try
    {
      UpdateDnsResult result = await this.dnsUpdateService.UpdateAsync (zone: resolution.Zone,
                                                                         name: resolution.Name,
                                                                         ipAddress: ipResolution.EffectiveIp,
                                                                         zoneConfig: zoneConfig,
                                                                         cancellationToken: cancellationToken);

      this.logger.LogInformation (message: result.Changed
                                              ? "Updated {RecordType} record {Fqdn} for client {Client} to {IpAddress}."
                                              : "{RecordType} record {Fqdn} already up to date for client {Client} ({IpAddress}); no write.",
                                  result.RecordType,
                                  LogSanitizer.Sanitize (result.Fqdn),
                                  LogSanitizer.Sanitize (client.Name),
                                  result.IpAddress);

      return new DdnsUpdateResult (Status: DdnsUpdateStatus.Success, Update: result);
    }
    catch (ArgumentException exception)
    {
      this.logger.LogWarning (exception: exception,
                              message: "Invalid DNS update request for client {Client}, hostname {Hostname}.",
                              LogSanitizer.Sanitize (client.Name),
                              LogSanitizer.Sanitize (hostname));

      return new DdnsUpdateResult (DdnsUpdateStatus.InvalidAddress);
    }
    catch (Exception exception) when (exception is RequestFailedException or AuthenticationFailedException)
    {
      // AuthenticationFailedException covers CredentialUnavailableException: the app's managed identity
      // could not obtain a token, which is a server-side fault just like an Azure DNS failure.
      this.logger.LogError (exception: exception,
                            message: "Azure DNS update failed for client {Client}, hostname {Hostname}.",
                            LogSanitizer.Sanitize (client.Name),
                            LogSanitizer.Sanitize (hostname));

      return new DdnsUpdateResult (DdnsUpdateStatus.DnsUpdateFailed);
    }
    catch (InvalidOperationException exception)
    {
      this.logger.LogError (exception: exception,
                            message: "Function configuration is invalid for client {Client}, hostname {Hostname}.",
                            LogSanitizer.Sanitize (client.Name),
                            LogSanitizer.Sanitize (hostname));

      return new DdnsUpdateResult (DdnsUpdateStatus.ServerMisconfigured);
    }
  }
}
