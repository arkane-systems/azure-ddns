#region header

// AzureDdns.FunctionApp.Tests - UpdateDnsFunctionTests.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-03-30 8:20 PM

#endregion

#region using

using AzureDdns.FunctionApp.Config;
using AzureDdns.FunctionApp.Functions;
using AzureDdns.FunctionApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using System.Net;

#endregion

namespace AzureDdns.FunctionApp.Tests;

public sealed class UpdateDnsFunctionTests
{
    #region Nested type: NoopDnsUpdateService

    private sealed class NoopDnsUpdateService : IDnsUpdateService
    {
        public Task<UpdateDnsResult> UpdateAsync (string zone,
                                                   string name,
                                                   IPAddress ipAddress,
                                                   ZoneConfig zoneConfig,
                                                   CancellationToken cancellationToken = default)
            => throw new InvalidOperationException ("Should not be called in validation tests.");
    }

    #endregion

    #region Nested type: StaticConfigProvider

    private sealed class StaticConfigProvider : IConfigProvider
    {
        public StaticConfigProvider (DyndnsConfig config) => this._config = config;

        private readonly DyndnsConfig _config ;

        public Task<DyndnsConfig> GetConfigAsync (CancellationToken cancellationToken = default)
            => Task.FromResult (this._config);
    }

    #endregion

    #region Nested type: StubAuthService

    private sealed class StubAuthService : IAuthService
    {
        public StubAuthService (bool isAuthorized, bool isRecordAuthorized)
        {
            this._isAuthorized = isAuthorized;
            this._isRecordAuthorized = isRecordAuthorized;
        }

        private readonly bool _isAuthorized ;
        private readonly bool _isRecordAuthorized ;

        public ClientConfig? Authenticate (string clientName, string rawKey, DyndnsConfig config)
            => this._isAuthorized ? new ClientConfig { Name = clientName } : null;

        public bool IsRecordAuthorized (ClientConfig client, string zone, string name) => this._isRecordAuthorized;
    }

    #endregion

    [Fact]
    public async Task RunAsync_ReturnsBadRequest_WhenClientMissing ()
    {
        UpdateDnsFunction function = CreateFunction (config: new DyndnsConfig (), isAuthorized: true) ;
        HttpRequest request = CreateRequest (new Dictionary <string, string?>
        {
            ["key"] = "k",
            ["zone"] = "example.com",
            ["name"] = "home",
        }) ;

        IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None) ;

        var content = Assert.IsType <ContentResult> (result) ;
        Assert.Equal (expected: StatusCodes.Status400BadRequest, actual: content.StatusCode);
        Assert.Equal (expected: "ERROR: missing client", actual: content.Content);
    }

    [Fact]
    public async Task RunAsync_ReturnsUnauthorized_WhenCredentialsInvalid ()
    {
        var config = new DyndnsConfig { Zones = { ["example.com"] = new ZoneConfig { Ttl = 300 }, }, } ;

        UpdateDnsFunction function = CreateFunction (config: config, isAuthorized: false) ;
        HttpRequest request = CreateRequest (new Dictionary <string, string?>
        {
            ["client"] = "home-router",
            ["key"]    = "bad",
            ["zone"]   = "example.com",
            ["name"]   = "home",
        }) ;

        IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None) ;

        var content = Assert.IsType <ContentResult> (result) ;
        Assert.Equal (expected: StatusCodes.Status401Unauthorized, actual: content.StatusCode);
        Assert.Equal (expected: "ERROR: invalid credentials", actual: content.Content);
    }

    [Fact]
    public async Task RunAsync_ReturnsForbidden_WhenRecordUnauthorized ()
    {
        var config = new DyndnsConfig { Zones = { ["example.com"] = new ZoneConfig { Ttl = 300 }, }, } ;

        UpdateDnsFunction function = CreateFunction (config: config, isAuthorized: true, isRecordAuthorized: false) ;
        HttpRequest request = CreateRequest (new Dictionary <string, string?>
        {
            ["client"] = "home-router",
            ["key"]    = "ok",
            ["zone"]   = "example.com",
            ["name"]   = "home",
        }) ;

        IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None) ;

        var content = Assert.IsType <ContentResult> (result) ;
        Assert.Equal (expected: StatusCodes.Status403Forbidden, actual: content.StatusCode);
        Assert.Equal (expected: "ERROR: unauthorized record", actual: content.Content);
    }

    private static UpdateDnsFunction CreateFunction (DyndnsConfig config, bool isAuthorized, bool isRecordAuthorized = true)
    {
        IConfigProvider   configProvider = new StaticConfigProvider (config) ;
        IAuthService      authService = new StubAuthService (isAuthorized: isAuthorized, isRecordAuthorized: isRecordAuthorized) ;
        IIpResolver       ipResolver = new IpResolver () ;
        IDnsUpdateService dnsUpdateService = new NoopDnsUpdateService () ;

        return new UpdateDnsFunction (configProvider: configProvider,
                                      authService: authService,
                                      ipResolver: ipResolver,
                                      dnsUpdateService: dnsUpdateService,
                                      logger: NullLogger<UpdateDnsFunction>.Instance);
    }

    private static HttpRequest CreateRequest (Dictionary<string, string?> query)
    {
        var context = new DefaultHttpContext () ;
        context.Connection.RemoteIpAddress = IPAddress.Parse ("203.0.113.10");

        Dictionary <string, StringValues> queryCollection =
            query.ToDictionary (keySelector: pair => pair.Key, elementSelector: pair => new StringValues (pair.Value)) ;
        context.Request.Query = new QueryCollection (queryCollection);

        return context.Request;
    }
}
