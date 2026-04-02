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
    private readonly IpResolver _resolver = new ();

    [Fact]
    public void Resolve_UsesSourceIp_WhenExplicitIpMissing ()
    {
        HttpRequest request = CreateRequest (remoteIp: "203.0.113.10");

        IpResolutionResult result = this._resolver.Resolve (request: request, explicitIp: null);

        Assert.Equal (expected: IPAddress.Parse ("203.0.113.10"), actual: result.EffectiveIp);
        Assert.Equal (expected: IPAddress.Parse ("203.0.113.10"), actual: result.SourceIp);
        Assert.False (result.ExplicitIpMismatch);
        Assert.Equal (expected: IPAddress.Parse ("203.0.113.10"), actual: result.Diagnostics.RemoteIp);
        Assert.False (result.Diagnostics.TrustedProxyHop);
        Assert.Null (result.Diagnostics.ForwardedForHeader);
    }

    [Fact]
    public void Resolve_UsesForwardedClientIp_WhenTrustedProxyHopProvidesHeader ()
    {
        HttpRequest request = CreateRequest (remoteIp: "127.0.0.1", forwardedFor: "198.51.100.25, 10.0.0.5");

        IpResolutionResult result = this._resolver.Resolve (request: request, explicitIp: null);

        Assert.Equal (expected: IPAddress.Parse ("198.51.100.25"), actual: result.EffectiveIp);
        Assert.Equal (expected: IPAddress.Parse ("198.51.100.25"), actual: result.SourceIp);
        Assert.True (result.Diagnostics.TrustedProxyHop);
        Assert.Equal (expected: IPAddress.Parse ("198.51.100.25"), actual: result.Diagnostics.ForwardedForIp);
        Assert.Equal (expected: "198.51.100.25, 10.0.0.5", actual: result.Diagnostics.ForwardedForHeader);
    }

    [Fact]
    public void Resolve_IgnoresForwardedClientIp_WhenImmediateHopIsNotTrusted ()
    {
        HttpRequest request = CreateRequest (remoteIp: "203.0.113.10", forwardedFor: "198.51.100.25");

        IpResolutionResult result = this._resolver.Resolve (request: request, explicitIp: null);

        Assert.Equal (expected: IPAddress.Parse ("203.0.113.10"), actual: result.EffectiveIp);
        Assert.Equal (expected: IPAddress.Parse ("203.0.113.10"), actual: result.SourceIp);
    }

    [Fact]
    public void Resolve_IgnoresInvalidForwardedClientIp_WhenTrustedProxyHopProvidesHeader ()
    {
        HttpRequest request = CreateRequest (remoteIp: "::1", forwardedFor: "bad-ip, also-bad");

        IpResolutionResult result = this._resolver.Resolve (request: request, explicitIp: null);

        Assert.Equal (expected: IPAddress.IPv6Loopback, actual: result.EffectiveIp);
        Assert.Equal (expected: IPAddress.IPv6Loopback, actual: result.SourceIp);
    }

    [Fact]
    public void Resolve_UsesExplicitIp_WhenValid ()
    {
        HttpRequest request = CreateRequest (remoteIp: "203.0.113.10");

        IpResolutionResult result = this._resolver.Resolve (request: request, explicitIp: "2001:db8::1");

        Assert.Equal (expected: IPAddress.Parse ("2001:db8::1"), actual: result.EffectiveIp);
        Assert.True (result.ExplicitIpMismatch);
    }

    [Fact]
    public void Resolve_FlagsMismatchAgainstForwardedClientIp_WhenExplicitIpDiffers ()
    {
        HttpRequest request = CreateRequest (remoteIp: "10.0.0.4", forwardedFor: "198.51.100.25");

        IpResolutionResult result = this._resolver.Resolve (request: request, explicitIp: "2001:db8::1");

        Assert.Equal (expected: IPAddress.Parse ("198.51.100.25"), actual: result.SourceIp);
        Assert.True (result.ExplicitIpMismatch);
    }

    [Fact]
    public void Resolve_ReturnsNullEffectiveIp_WhenExplicitIpInvalid ()
    {
        HttpRequest request = CreateRequest (remoteIp: "203.0.113.10");

        IpResolutionResult result = this._resolver.Resolve (request: request, explicitIp: "bad-ip");

        Assert.Null (result.EffectiveIp);
        Assert.Equal (expected: IPAddress.Parse ("203.0.113.10"), actual: result.SourceIp);
    }

    [Fact]
    public void Resolve_UsesClientIpHeader_WhenTrustedProxyHopAndForwardedForMissing ()
    {
        HttpRequest request = CreateRequest (remoteIp: "::1");
        request.Headers["CLIENT-IP"] = "99.87.210.81:55096";

        IpResolutionResult result = this._resolver.Resolve (request: request, explicitIp: null);

        Assert.Equal (expected: IPAddress.Parse ("99.87.210.81"), actual: result.EffectiveIp);
        Assert.Equal (expected: IPAddress.Parse ("99.87.210.81"), actual: result.SourceIp);
        Assert.Equal (expected: IPAddress.Parse ("99.87.210.81"), actual: result.Diagnostics.ClientIp);
        Assert.Equal (expected: "99.87.210.81:55096", actual: result.Diagnostics.ClientIpHeader);
    }

    private static HttpRequest CreateRequest (string remoteIp, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext ();
        context.Connection.RemoteIpAddress = IPAddress.Parse (remoteIp);

        if (!string.IsNullOrWhiteSpace (forwardedFor))
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;

        return context.Request;
    }
}
