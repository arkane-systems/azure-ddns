#region header

// AzureDdns.FunctionApp.Tests - IpResolverTests.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-03-30 8:20 PM

#endregion

#region using

using AzureDdns.FunctionApp.Services;
using Microsoft.AspNetCore.Http;
using System.Net;

#endregion

namespace AzureDdns.FunctionApp.Tests;

public sealed class IpResolverTests
{
    private readonly IpResolver _resolver = new () ;

    [Fact]
    public void Resolve_UsesSourceIp_WhenExplicitIpMissing ()
    {
        HttpRequest request = CreateRequest ("203.0.113.10") ;

        IpResolutionResult result = this._resolver.Resolve (request: request, explicitIp: null) ;

        Assert.Equal (expected: IPAddress.Parse ("203.0.113.10"), actual: result.EffectiveIp);
        Assert.False (result.ExplicitIpMismatch);
    }

    [Fact]
    public void Resolve_UsesExplicitIp_WhenValid ()
    {
        HttpRequest request = CreateRequest ("203.0.113.10") ;

        IpResolutionResult result = this._resolver.Resolve (request: request, explicitIp: "2001:db8::1") ;

        Assert.Equal (expected: IPAddress.Parse ("2001:db8::1"), actual: result.EffectiveIp);
        Assert.True (result.ExplicitIpMismatch);
    }

    [Fact]
    public void Resolve_ReturnsNullEffectiveIp_WhenExplicitIpInvalid ()
    {
        HttpRequest request = CreateRequest ("203.0.113.10") ;

        IpResolutionResult result = this._resolver.Resolve (request: request, explicitIp: "bad-ip") ;

        Assert.Null (result.EffectiveIp);
        Assert.Equal (expected: IPAddress.Parse ("203.0.113.10"), actual: result.SourceIp);
    }

    private static HttpRequest CreateRequest (string remoteIp)
    {
        var context = new DefaultHttpContext () ;
        context.Connection.RemoteIpAddress = IPAddress.Parse (remoteIp);

        return context.Request;
    }
}
