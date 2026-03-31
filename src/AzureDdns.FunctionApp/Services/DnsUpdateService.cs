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
    Task<UpdateDnsResult> UpdateAsync (string zone,
                                        string name,
                                        IPAddress ipAddress,
                                        ZoneConfig zoneConfig,
                                        CancellationToken cancellationToken = default);
}

public sealed class DnsUpdateService : IDnsUpdateService
{
    public DnsUpdateService (IOptions<RuntimeSettings> runtimeSettings)
    {
        this._settings = runtimeSettings.Value;
        this._armClient = new ArmClient (new DefaultAzureCredential ());
    }

    private readonly ArmClient       _armClient;
    private readonly RuntimeSettings _settings;

    public async Task<UpdateDnsResult> UpdateAsync (string zone,
                                                     string name,
                                                     IPAddress ipAddress,
                                                     ZoneConfig zoneConfig,
                                                     CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace (this._settings.DnsSubscriptionId) ||
            string.IsNullOrWhiteSpace (this._settings.DnsResourceGroup))
            throw new InvalidOperationException ("DNS_SUBSCRIPTION_ID and DNS_RESOURCE_GROUP must be configured.");

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

    private static string ToFqdn (string name, string zone)
        => string.Equals (a: name, b: "@", comparisonType: StringComparison.Ordinal) ? zone : $"{name}.{zone}";
}

public sealed record UpdateDnsResult (string RecordType, string Fqdn, string IpAddress);
