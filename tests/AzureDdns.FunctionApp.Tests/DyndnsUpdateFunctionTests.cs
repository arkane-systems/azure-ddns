#region header

// AzureDdns.FunctionApp.Tests - DyndnsUpdateFunctionTests.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-04-18 12:00 AM

#endregion

#region using

using System.Net;
using System.Text;

using Azure;

using AzureDdns.FunctionApp.Config;
using AzureDdns.FunctionApp.Functions;
using AzureDdns.FunctionApp.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

#endregion

namespace AzureDdns.FunctionApp.Tests;

public sealed class DyndnsUpdateFunctionTests
{
  #region Nested type: NoopDnsUpdateService

  private sealed class NoopDnsUpdateService : IDnsUpdateService
  {
    public Task<UpdateDnsResult> UpdateAsync (string            zone,
                                              string            name,
                                              IPAddress         ipAddress,
                                              ZoneConfig        zoneConfig,
                                              CancellationToken cancellationToken = default)
      => throw new InvalidOperationException ("Should not be called in validation tests.");
  }

  #endregion

  #region Nested type: StaticConfigProvider

  private sealed class StaticConfigProvider (DyndnsConfig config) : IConfigProvider
  {
    public Task<DyndnsConfig> GetConfigAsync (CancellationToken cancellationToken = default)
      => Task.FromResult (config);
  }

  #endregion

  #region Nested type: StubAuthService

  private sealed class StubAuthService (bool isAuthorized, bool isRecordAuthorized = true) : IAuthService
  {
    public ClientConfig? Authenticate (string clientName, string rawKey, DyndnsConfig config)
      => isAuthorized ? new ClientConfig { Name = clientName } : null;

    public bool IsRecordAuthorized (ClientConfig client, string zone, string name) => isRecordAuthorized;
  }

  #endregion

  #region Nested type: StubDnsUpdateService

  private sealed class StubDnsUpdateService (UpdateDnsResult? result = null, Exception? exception = null)
    : IDnsUpdateService
  {
    public Task<UpdateDnsResult> UpdateAsync (string            zone,
                                              string            name,
                                              IPAddress         ipAddress,
                                              ZoneConfig        zoneConfig,
                                              CancellationToken cancellationToken = default)
      => exception is not null ? throw exception : Task.FromResult (result!);
  }

  #endregion

  #region Nested type: StubFqdnResolver

  private sealed class StubFqdnResolver (FqdnResolution? resolution) : IFqdnResolver
  {
    public FqdnResolution? Resolve (string hostname, IReadOnlyDictionary<string, ZoneConfig> zones)
      => resolution;
  }

  #endregion

  // ── test cases ────────────────────────────────────────────────────────────────────────────

  [Fact]
  public async Task RunAsync_ReturnsBadauth_WhenNoAuthorizationHeader ()
  {
    DyndnsUpdateFunction function = CreateFunction ();
    HttpRequest          request  = CreateRequest (query: new Dictionary<string, string?> { ["hostname"] = "home.example.com", });

    IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None);

    var content = Assert.IsType<ContentResult> (result);
    Assert.Equal (expected: StatusCodes.Status401Unauthorized, actual: content.StatusCode);
    Assert.Equal (expected: "badauth",                         actual: content.Content);
  }

  [Fact]
  public async Task RunAsync_ReturnsBadauth_WhenAuthHeaderIsMalformedBase64 ()
  {
    DyndnsUpdateFunction function = CreateFunction ();
    HttpRequest request = CreateRequest (query: new Dictionary<string, string?> { ["hostname"] = "home.example.com", },
                                         authHeader: "Basic not-valid-base64!!!");

    IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None);

    var content = Assert.IsType<ContentResult> (result);
    Assert.Equal (expected: StatusCodes.Status401Unauthorized, actual: content.StatusCode);
    Assert.Equal (expected: "badauth",                         actual: content.Content);
  }

  [Fact]
  public async Task RunAsync_ReturnsBadauth_WhenCredentialsInvalid ()
  {
    var config = BuildConfig ();
    DyndnsUpdateFunction function = CreateFunction (config: config,
                                                    isAuthorized: false,
                                                    fqdnResolution: new FqdnResolution (Zone: "example.com", Name: "home"));
    HttpRequest request = CreateRequest (query: new Dictionary<string, string?> { ["hostname"] = "home.example.com", },
                                         authHeader: MakeBasicAuth ("client", "wrong-key"));

    IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None);

    var content = Assert.IsType<ContentResult> (result);
    Assert.Equal (expected: StatusCodes.Status401Unauthorized, actual: content.StatusCode);
    Assert.Equal (expected: "badauth",                         actual: content.Content);
  }

  [Fact]
  public async Task RunAsync_ReturnsBadauth_WhenCredentialsInvalid_AndHostnameUnknown ()
  {
    var config = BuildConfig ();
    DyndnsUpdateFunction function = CreateFunction (config: config, isAuthorized: false, fqdnResolution: null);
    HttpRequest request = CreateRequest (query: new Dictionary<string, string?> { ["hostname"] = "home.other.com", },
                                         authHeader: MakeBasicAuth ("client", "wrong-key"));

    IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None);

    var content = Assert.IsType<ContentResult> (result);
    Assert.Equal (expected: StatusCodes.Status401Unauthorized, actual: content.StatusCode);
    Assert.Equal (expected: "badauth",                         actual: content.Content);
  }

  [Fact]
  public async Task RunAsync_ReturnsNohost_WhenHostnameMissing ()
  {
    DyndnsUpdateFunction function = CreateFunction (isAuthorized: true);
    // No hostname query parameter supplied.
    HttpRequest request = CreateRequest (query: new Dictionary<string, string?> { },
                                         authHeader: MakeBasicAuth ("client", "key"));

    IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None);

    var content = Assert.IsType<ContentResult> (result);
    Assert.Equal (expected: StatusCodes.Status200OK, actual: content.StatusCode);
    Assert.Equal (expected: "nohost",                actual: content.Content);
  }

  [Fact]
  public async Task RunAsync_ReturnsNohost_WhenFqdnNotResolvable ()
  {
    // Stub FQDN resolver returns null (hostname not in config).
    var config = BuildConfig ();
    DyndnsUpdateFunction function = CreateFunction (config: config, isAuthorized: true, fqdnResolution: null);
    HttpRequest request = CreateRequest (query: new Dictionary<string, string?> { ["hostname"] = "home.other.com", },
                                         authHeader: MakeBasicAuth ("client", "key"));

    IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None);

    var content = Assert.IsType<ContentResult> (result);
    Assert.Equal (expected: StatusCodes.Status200OK, actual: content.StatusCode);
    Assert.Equal (expected: "nohost",                actual: content.Content);
  }

  [Fact]
  public async Task RunAsync_ReturnsNohost_WhenRecordNotAuthorized ()
  {
    var config = BuildConfig ();
    DyndnsUpdateFunction function = CreateFunction (config: config,
                                                    isAuthorized: true,
                                                    isRecordAuthorized: false,
                                                    fqdnResolution: new FqdnResolution (Zone: "example.com", Name: "home"));
    HttpRequest request = CreateRequest (query: new Dictionary<string, string?> { ["hostname"] = "home.example.com", },
                                         authHeader: MakeBasicAuth ("client", "key"));

    IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None);

    var content = Assert.IsType<ContentResult> (result);
    Assert.Equal (expected: StatusCodes.Status200OK, actual: content.StatusCode);
    Assert.Equal (expected: "nohost",                actual: content.Content);
  }

  [Fact]
  public async Task RunAsync_ReturnsGood_WhenUpdateSucceeds ()
  {
    var config    = BuildConfig ();
    var dnsResult = new UpdateDnsResult (RecordType: "A", Fqdn: "home.example.com", IpAddress: "203.0.113.10");

    DyndnsUpdateFunction function = CreateFunction (config: config,
                                                    isAuthorized: true,
                                                    fqdnResolution: new FqdnResolution (Zone: "example.com", Name: "home"),
                                                    dnsUpdateResult: dnsResult);
    HttpRequest request = CreateRequest (query: new Dictionary<string, string?>
                                                { ["hostname"] = "home.example.com", ["myip"] = "203.0.113.10", },
                                         authHeader: MakeBasicAuth ("client", "key"));

    IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None);

    var content = Assert.IsType<ContentResult> (result);
    Assert.Equal (expected: StatusCodes.Status200OK,    actual: content.StatusCode);
    Assert.Equal (expected: "good 203.0.113.10", actual: content.Content);
  }

  [Fact]
  public async Task RunAsync_ReturnsGood_WithIpv6Address ()
  {
    var config    = BuildConfig ();
    var dnsResult = new UpdateDnsResult (RecordType: "AAAA", Fqdn: "home.example.com", IpAddress: "2001:db8::1");

    DyndnsUpdateFunction function = CreateFunction (config: config,
                                                    isAuthorized: true,
                                                    fqdnResolution: new FqdnResolution (Zone: "example.com", Name: "home"),
                                                    dnsUpdateResult: dnsResult);
    HttpRequest request = CreateRequest (query: new Dictionary<string, string?>
                                                { ["hostname"] = "home.example.com", ["myip"] = "2001:db8::1", },
                                         authHeader: MakeBasicAuth ("client", "key"));

    IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None);

    var content = Assert.IsType<ContentResult> (result);
    Assert.Equal (expected: StatusCodes.Status200OK, actual: content.StatusCode);
    Assert.Equal (expected: "good 2001:db8::1", actual: content.Content);
  }

  [Fact]
  public async Task RunAsync_ReturnsServerError_WhenDnsUpdateFails ()
  {
    var config         = BuildConfig ();
    var azureException = new RequestFailedException (status: 503, message: "Service unavailable");

    DyndnsUpdateFunction function = CreateFunction (config: config,
                                                    isAuthorized: true,
                                                    fqdnResolution: new FqdnResolution (Zone: "example.com", Name: "home"),
                                                    dnsUpdateException: azureException);
    HttpRequest request = CreateRequest (query: new Dictionary<string, string?>
                                                { ["hostname"] = "home.example.com", ["myip"] = "203.0.113.10", },
                                         authHeader: MakeBasicAuth ("client", "key"));

    IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None);

    var content = Assert.IsType<ContentResult> (result);
    Assert.Equal (expected: StatusCodes.Status200OK, actual: content.StatusCode);
    Assert.Equal (expected: "911",                   actual: content.Content);
  }

  [Fact]
  public async Task RunAsync_ReturnsServerError_WhenIpNotResolvable ()
  {
    var config = BuildConfig ();

    DyndnsUpdateFunction function = CreateFunction (config: config,
                                                    isAuthorized: true,
                                                    fqdnResolution: new FqdnResolution (Zone: "example.com", Name: "home"));
    // Provide a syntactically invalid IP so IpResolver returns EffectiveIp = null.
    HttpRequest request = CreateRequest (query: new Dictionary<string, string?>
                                                { ["hostname"] = "home.example.com", ["myip"] = "not-an-ip", },
                                         authHeader: MakeBasicAuth ("client", "key"),
                                         remoteIp: null);

    IActionResult result = await function.RunAsync (request: request, cancellationToken: CancellationToken.None);

    var content = Assert.IsType<ContentResult> (result);
    Assert.Equal (expected: StatusCodes.Status200OK, actual: content.StatusCode);
    Assert.Equal (expected: "911",                   actual: content.Content);
  }

  // ── factory helpers ───────────────────────────────────────────────────────────────────────

  private static DyndnsUpdateFunction CreateFunction (DyndnsConfig?    config             = null,
                                                      bool             isAuthorized        = true,
                                                      bool             isRecordAuthorized  = true,
                                                      FqdnResolution?  fqdnResolution      = null,
                                                      UpdateDnsResult? dnsUpdateResult     = null,
                                                      Exception?       dnsUpdateException  = null)
  {
    config ??= new DyndnsConfig ();

    IConfigProvider   configProvider   = new StaticConfigProvider (config);
    IAuthService      authService      = new StubAuthService (isAuthorized: isAuthorized, isRecordAuthorized: isRecordAuthorized);
    IFqdnResolver     fqdnResolver     = new StubFqdnResolver (resolution: fqdnResolution);
    IIpResolver       ipResolver       = new IpResolver ();
    IDnsUpdateService dnsUpdateService = dnsUpdateResult is not null || dnsUpdateException is not null
                                           ? new StubDnsUpdateService (result: dnsUpdateResult, exception: dnsUpdateException)
                                           : new NoopDnsUpdateService ();

    return new DyndnsUpdateFunction (configProvider: configProvider,
                                     authService: authService,
                                     fqdnResolver: fqdnResolver,
                                     ipResolver: ipResolver,
                                     dnsUpdateService: dnsUpdateService,
                                     logger: NullLogger<DyndnsUpdateFunction>.Instance);
  }

  private static HttpRequest CreateRequest (Dictionary<string, string?> query,
                                            string?                     authHeader = null,
                                            string?                     remoteIp   = "203.0.113.10")
  {
    var context = new DefaultHttpContext ();

    if (remoteIp is not null)
      context.Connection.RemoteIpAddress = IPAddress.Parse (remoteIp);

    Dictionary<string, StringValues> queryCollection =
      query.ToDictionary (keySelector: pair => pair.Key,
                          elementSelector: pair => new StringValues (pair.Value));
    context.Request.Query = new QueryCollection (queryCollection);

    if (authHeader is not null)
      context.Request.Headers["Authorization"] = authHeader;

    return context.Request;
  }

  private static DyndnsConfig BuildConfig ()
    => new () { Zones = { ["example.com"] = new ZoneConfig { Ttl = 300 }, }, };

  private static string MakeBasicAuth (string username, string password)
    => "Basic " + Convert.ToBase64String (Encoding.UTF8.GetBytes ($"{username}:{password}"));
}
