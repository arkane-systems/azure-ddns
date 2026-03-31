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

using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Dns;
using Azure.ResourceManager.Dns.Models;
using AzureDdns.FunctionApp.Config;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;

#endregion

namespace AzureDdns.FunctionApp.Services;

public interface IDnsUpdateService
{
    /// <summary>
    ///     Upserts a DNS record in Azure DNS based on the supplied IP address family.
    /// </summary>
    /// <param name="zone">Target DNS zone.</param>
    /// <param name="name">Relative record name (or <c>@</c> for zone apex).</param>
    /// <param name="ipAddress">Resolved IP address to write.</param>
    /// <param name="zoneConfig">Zone-specific settings such as TTL.</param>
    /// <param name="cancellationToken">Cancellation token for Azure SDK operations.</param>
    /// <returns>Summary of the record type/FQDN/IP written.</returns>
    Task<UpdateDnsResult> UpdateAsync (string zone,
                                        string name,
                                        IPAddress ipAddress,
                                        ZoneConfig zoneConfig,
                                        CancellationToken cancellationToken = default);
}

/// <summary>
///     Writes DNS <c>A</c> or <c>AAAA</c> records to Azure DNS using managed identity credentials.
/// </summary>
public sealed class DnsUpdateService : IDnsUpdateService
{
    public DnsUpdateService (IOptions<RuntimeSettings> runtimeSettings)
    {
        this._settings = runtimeSettings.Value;

        // DefaultAzureCredential allows local dev (developer identity) and Azure-hosted managed identity.
        this._armClient = new ArmClient (new DefaultAzureCredential ());
    }

    private readonly ArmClient       _armClient;
    private readonly RuntimeSettings _settings;

    /// <summary>
    ///     Creates or updates a single DNS record set matching the IP address family.
    /// </summary>
    /// <remarks>
    ///     This method intentionally updates only one record family per request:
    ///     IPv4 -> A, IPv6 -> AAAA. The opposite record family is left untouched.
    /// </remarks>
    public async Task<UpdateDnsResult> UpdateAsync (string zone,
                                                     string name,
                                                     IPAddress ipAddress,
                                                     ZoneConfig zoneConfig,
                                                     CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace (this._settings.DnsSubscriptionId) ||
            string.IsNullOrWhiteSpace (this._settings.DnsResourceGroup))
            throw new InvalidOperationException ("DNS_SUBSCRIPTION_ID and DNS_RESOURCE_GROUP must be configured.");

        // Normalize user input so updates are stable regardless of caller formatting.
        string normalizedZone = zone.Trim ().TrimEnd ('.');
        string relativeName   = string.IsNullOrWhiteSpace (name) ? "@" : name.Trim ();
        long   ttl            = zoneConfig.Ttl > 0 ? zoneConfig.Ttl : 300;

        ResourceIdentifier? zoneId = DnsZoneResource.CreateResourceIdentifier (subscriptionId: this._settings.DnsSubscriptionId,
                                                                               resourceGroupName: this._settings.DnsResourceGroup,
                                                                               zoneName: normalizedZone);
        DnsZoneResource zoneResource = this._armClient.GetDnsZoneResource (zoneId);

        switch (ipAddress.AddressFamily)
        {
            case AddressFamily.InterNetwork:
                var aData = new DnsARecordData { TtlInSeconds           = ttl, };
                aData.DnsARecords.Add (new DnsARecordInfo { IPv4Address = ipAddress });

                await zoneResource.GetDnsARecords ()
                                  .CreateOrUpdateAsync (waitUntil: WaitUntil.Completed,
                                                        aRecordName: relativeName,
                                                        data: aData,
                                                        cancellationToken: cancellationToken);

                return new UpdateDnsResult (RecordType: "A",
                                            Fqdn: ToFqdn (name: relativeName, zone: normalizedZone),
                                            IpAddress: ipAddress.ToString ());

            case AddressFamily.InterNetworkV6:
                var aaaaData = new DnsAaaaRecordData { TtlInSeconds              = ttl, };
                aaaaData.DnsAaaaRecords.Add (new DnsAaaaRecordInfo { IPv6Address = ipAddress });

                await zoneResource.GetDnsAaaaRecords ()
                                  .CreateOrUpdateAsync (waitUntil: WaitUntil.Completed,
                                                        aaaaRecordName: relativeName,
                                                        data: aaaaData,
                                                        cancellationToken: cancellationToken);

                return new UpdateDnsResult (RecordType: "AAAA",
                                            Fqdn: ToFqdn (name: relativeName, zone: normalizedZone),
                                            IpAddress: ipAddress.ToString ());

            default:
                throw new ArgumentException (message: "Only IPv4 and IPv6 addresses are supported.",
                                             paramName: nameof (ipAddress));
        }
    }

    /// <summary>
    ///     Builds a canonical FQDN for logging/response output.
    /// </summary>
    private static string ToFqdn (string name, string zone)
        => string.Equals (a: name, b: "@", comparisonType: StringComparison.Ordinal) ? zone : $"{name}.{zone}";
}

/// <summary>
///     Lightweight result model describing the DNS update performed.
/// </summary>
/// <param name="RecordType">Updated record type (<c>A</c> or <c>AAAA</c>).</param>
/// <param name="Fqdn">Fully qualified DNS name that was updated.</param>
/// <param name="IpAddress">IP value written to the record set.</param>
public sealed record UpdateDnsResult (string RecordType, string Fqdn, string IpAddress);
