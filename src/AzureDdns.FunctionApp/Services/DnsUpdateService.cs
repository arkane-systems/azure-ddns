using System.Net;
using AzureDdns.FunctionApp.Config;

namespace AzureDdns.FunctionApp.Services;

public interface IDnsUpdateService
{
    Task<UpdateDnsResult> UpdateAsync(string zone, string name, IPAddress ipAddress, ZoneConfig zoneConfig, CancellationToken cancellationToken = default);
}

public sealed class DnsUpdateService : IDnsUpdateService
{
    public Task<UpdateDnsResult> UpdateAsync(string zone, string name, IPAddress ipAddress, ZoneConfig zoneConfig, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException("Azure DNS update behavior has not been implemented yet.");
    }
}

public sealed record UpdateDnsResult(string RecordType, string Fqdn, string IpAddress);
