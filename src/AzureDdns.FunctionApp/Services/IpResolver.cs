#region header

// AzureDdns.FunctionApp - IpResolver.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-03-30 7:41 PM

#endregion

#region using

using Microsoft.AspNetCore.Http;
using System.Net;

#endregion

namespace AzureDdns.FunctionApp.Services;

public interface IIpResolver
{
    IpResolutionResult Resolve (HttpRequest request, string? explicitIp);
}

public sealed class IpResolver : IIpResolver
{
    public IpResolutionResult Resolve (HttpRequest request, string? explicitIp)
    {
        IPAddress? sourceIp = request.HttpContext.Connection.RemoteIpAddress ;

        if (string.IsNullOrWhiteSpace (explicitIp))
            return new IpResolutionResult (EffectiveIp: sourceIp, SourceIp: sourceIp, ExplicitIpMismatch: false);

        if (!IPAddress.TryParse (ipString: explicitIp, address: out IPAddress? parsedExplicitIp))
            return new IpResolutionResult (EffectiveIp: null, SourceIp: sourceIp, ExplicitIpMismatch: false);

        bool mismatch = sourceIp is not null && !sourceIp.Equals (parsedExplicitIp) ;

        return new IpResolutionResult (EffectiveIp: parsedExplicitIp, SourceIp: sourceIp, ExplicitIpMismatch: mismatch);
    }
}

public sealed record IpResolutionResult (IPAddress? EffectiveIp, IPAddress? SourceIp, bool ExplicitIpMismatch);
