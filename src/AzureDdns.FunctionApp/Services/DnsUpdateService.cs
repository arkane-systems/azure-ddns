#region header

// AzureDdns.FunctionApp - DnsUpdateService.cs
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

using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;

using AzureDdns.FunctionApp.Config;

using Microsoft.Extensions.Options;

#endregion

namespace AzureDdns.FunctionApp.Services;

public interface IDnsUpdateService
{
  /// <summary>
  ///   Upserts a DNS record in Azure DNS based on the supplied IP address family.
  /// </summary>
  /// <param name="zone">Target DNS zone.</param>
  /// <param name="name">Relative record name (or <c>@</c> for zone apex).</param>
  /// <param name="ipAddress">Resolved IP address to write.</param>
  /// <param name="zoneConfig">Zone-specific settings such as TTL.</param>
  /// <param name="cancellationToken">Cancellation token for Azure SDK operations.</param>
  /// <returns>Summary of the record type/FQDN/IP written.</returns>
  Task<UpdateDnsResult> UpdateAsync (string            zone,
                                     string            name,
                                     IPAddress         ipAddress,
                                     ZoneConfig        zoneConfig,
                                     CancellationToken cancellationToken = default);
}

/// <summary>
///   Writes DNS <c>A</c> or <c>AAAA</c> records to Azure DNS using managed identity credentials.
/// </summary>
public sealed class DnsUpdateService (IOptions<RuntimeSettings> runtimeSettings) : IDnsUpdateService
{
  // DefaultAzureCredential allows local dev (developer identity) and Azure-hosted managed identity.

  private readonly ArmClient       armClient = new (new DefaultAzureCredential ());
  private readonly RuntimeSettings settings  = runtimeSettings.Value;

  /// <summary>
  ///   Creates or updates a single DNS record set matching the IP address family, writing only when needed.
  /// </summary>
  /// <remarks>
  ///   This method intentionally updates only one record family per request:
  ///   IPv4 -> A, IPv6 -> AAAA. The opposite record family is left untouched.
  ///   The existing record set is read first; if it already holds exactly the desired address and TTL
  ///   (see <see cref="DnsRecordComparison" />) nothing is written and the result has
  ///   <see cref="UpdateDnsResult.Changed" /> set to <see langword="false" />. DDNS clients typically report
  ///   their address every few minutes, so this avoids almost all Azure DNS writes (and activity-log noise).
  /// </remarks>
  public async Task<UpdateDnsResult> UpdateAsync (string            zone,
                                                  string            name,
                                                  IPAddress         ipAddress,
                                                  ZoneConfig        zoneConfig,
                                                  CancellationToken cancellationToken = default)
  {
    if (string.IsNullOrWhiteSpace (this.settings.DnsSubscriptionId) ||
        string.IsNullOrWhiteSpace (this.settings.DnsResourceGroup))
      throw new InvalidOperationException ("DNS_SUBSCRIPTION_ID and DNS_RESOURCE_GROUP must be configured.");

    // Normalize user input so updates are stable regardless of caller formatting.
    string normalizedZone = zone.Trim ().TrimEnd ('.');
    string relativeName   = string.IsNullOrWhiteSpace (name) ? "@" : name.Trim ();
    long   ttl            = zoneConfig.Ttl > 0 ? zoneConfig.Ttl : 300;

    ResourceIdentifier? zoneId = DnsZoneResource.CreateResourceIdentifier (subscriptionId: this.settings.DnsSubscriptionId,
                                                                           resourceGroupName: this.settings.DnsResourceGroup,
                                                                           zoneName: normalizedZone);
    DnsZoneResource zoneResource = this.armClient.GetDnsZoneResource (zoneId);

    switch (ipAddress.AddressFamily)
    {
      case AddressFamily.InterNetwork:
      {
        string fqdn = ToFqdn (name: relativeName, zone: normalizedZone);

        DnsARecordResource? existing = null;

        try
        {
          existing = await zoneResource.GetDnsARecords ().GetAsync (relativeName, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
          // No record set yet: nothing to compare with, so fall through and create it.
        }

        if (existing is not null &&
            DnsRecordComparison.IsCurrent (existingAddresses: existing.Data.DnsARecords.Select (record => record.IPv4Address),
                                           existingTtl: existing.Data.TtlInSeconds,
                                           desiredAddress: ipAddress,
                                           desiredTtl: ttl))
          return new UpdateDnsResult (RecordType: "A", Fqdn: fqdn, IpAddress: ipAddress.ToString (), Changed: false);

        var aData = new DnsARecordData { TtlInSeconds           = ttl, };
        aData.DnsARecords.Add (new DnsARecordInfo { IPv4Address = ipAddress });

        await zoneResource.GetDnsARecords ()
                          .CreateOrUpdateAsync (waitUntil: WaitUntil.Completed,
                                                aRecordName: relativeName,
                                                data: aData,
                                                cancellationToken: cancellationToken);

        return new UpdateDnsResult (RecordType: "A", Fqdn: fqdn, IpAddress: ipAddress.ToString ());
      }

      case AddressFamily.InterNetworkV6:
      {
        string fqdn = ToFqdn (name: relativeName, zone: normalizedZone);

        DnsAaaaRecordResource? existing = null;

        try
        {
          existing = await zoneResource.GetDnsAaaaRecords ().GetAsync (relativeName, cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
          // No record set yet: nothing to compare with, so fall through and create it.
        }

        if (existing is not null &&
            DnsRecordComparison.IsCurrent (existingAddresses: existing.Data.DnsAaaaRecords.Select (record => record.IPv6Address),
                                           existingTtl: existing.Data.TtlInSeconds,
                                           desiredAddress: ipAddress,
                                           desiredTtl: ttl))
          return new UpdateDnsResult (RecordType: "AAAA", Fqdn: fqdn, IpAddress: ipAddress.ToString (), Changed: false);

        var aaaaData = new DnsAaaaRecordData { TtlInSeconds              = ttl, };
        aaaaData.DnsAaaaRecords.Add (new DnsAaaaRecordInfo { IPv6Address = ipAddress });

        await zoneResource.GetDnsAaaaRecords ()
                          .CreateOrUpdateAsync (waitUntil: WaitUntil.Completed,
                                                aaaaRecordName: relativeName,
                                                data: aaaaData,
                                                cancellationToken: cancellationToken);

        return new UpdateDnsResult (RecordType: "AAAA", Fqdn: fqdn, IpAddress: ipAddress.ToString ());
      }

      default:
        throw new ArgumentException (message: "Only IPv4 and IPv6 addresses are supported.",
                                     paramName: nameof (ipAddress));
    }
  }

  /// <summary>
  ///   Builds a canonical FQDN for logging/response output.
  /// </summary>
  private static string ToFqdn (string name, string zone)
    => string.Equals (a: name, b: "@", comparisonType: StringComparison.Ordinal) ? zone : $"{name}.{zone}";
}

/// <summary>
///   Lightweight result model describing the DNS update performed.
/// </summary>
/// <param name="RecordType">Updated record type (<c>A</c> or <c>AAAA</c>).</param>
/// <param name="Fqdn">Fully qualified DNS name that was updated.</param>
/// <param name="IpAddress">IP value now held by the record set.</param>
/// <param name="Changed">
///   <see langword="true" /> if the record set was written; <see langword="false" /> if it already held exactly
///   this address and TTL and the write was skipped.
/// </param>
public sealed record UpdateDnsResult (string RecordType, string Fqdn, string IpAddress, bool Changed = true);
