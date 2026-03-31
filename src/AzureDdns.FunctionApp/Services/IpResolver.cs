using System.Net;
using Microsoft.AspNetCore.Http;

namespace AzureDdns.FunctionApp.Services;

public interface IIpResolver
{
    IpResolutionResult Resolve(HttpRequest request, string? explicitIp);
}

public sealed class IpResolver : IIpResolver
{
    public IpResolutionResult Resolve(HttpRequest request, string? explicitIp)
    {
        var sourceIp = request.HttpContext.Connection.RemoteIpAddress;

        if (string.IsNullOrWhiteSpace(explicitIp))
        {
            return new IpResolutionResult(sourceIp, sourceIp, false);
        }

        if (!IPAddress.TryParse(explicitIp, out var parsedExplicitIp))
        {
            return new IpResolutionResult(null, sourceIp, false);
        }

        var mismatch = sourceIp is not null && !sourceIp.Equals(parsedExplicitIp);
        return new IpResolutionResult(parsedExplicitIp, sourceIp, mismatch);
    }
}

public sealed record IpResolutionResult(IPAddress? EffectiveIp, IPAddress? SourceIp, bool ExplicitIpMismatch);
