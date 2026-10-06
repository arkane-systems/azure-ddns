#region header

// AzureDdns.FunctionApp.Tests - DdnsUpdateCoordinatorTests.cs
//
// Alistair J. R. Young
// Arkane Systems
//
// Copyright Arkane Systems 2012-2018.  All rights reserved.
//
// Created: 2026-10-06 4:30 PM

#endregion

#region using

using System.Net;
using System.Net.Sockets;

using Azure;
using Azure.Identity;

using AzureDdns.FunctionApp.Config;
using AzureDdns.FunctionApp.Services;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

#endregion

namespace AzureDdns.FunctionApp.Tests;

public sealed class DdnsUpdateCoordinatorTests
{
  private const string Hostname = "home.example.com";

  public static TheoryData<Exception, DdnsUpdateStatus> DnsFailures
    => new ()
       {
         { new RequestFailedException (status: 503, message: "unavailable"), DdnsUpdateStatus.DnsUpdateFailed },
         { new CredentialUnavailableException ("no managed identity"), DdnsUpdateStatus.DnsUpdateFailed },
         { new AuthenticationFailedException ("token request failed"), DdnsUpdateStatus.DnsUpdateFailed },
         { new ArgumentException ("unsupported address family"), DdnsUpdateStatus.InvalidAddress },
         { new InvalidOperationException ("DNS_SUBSCRIPTION_ID must be configured."), DdnsUpdateStatus.ServerMisconfigured },
       };

  #region Nested type: RecordingDnsUpdateService

  /// <summary>DNS stub that records the address it was asked to write, or throws a configured exception.</summary>
  private sealed class RecordingDnsUpdateService (Exception? exception = null, bool changed = true) : IDnsUpdateService
  {
    public IPAddress? WrittenAddress { get; private set; }

    public Task<UpdateDnsResult> UpdateAsync (string            zone,
                                              string            name,
                                              IPAddress         ipAddress,
                                              ZoneConfig        zoneConfig,
                                              CancellationToken cancellationToken = default)
    {
      this.WrittenAddress = ipAddress;

      if (exception is not null)
        throw exception;

      string recordType = ipAddress.AddressFamily == AddressFamily.InterNetwork ? "A" : "AAAA";

      return Task.FromResult (new UpdateDnsResult (RecordType: recordType,
                                                   Fqdn: $"{name}.{zone}",
                                                   IpAddress: ipAddress.ToString (),
                                                   Changed: changed));
    }
  }

  #endregion

  #region Nested type: StubAuthService

  private sealed class StubAuthService (bool authenticates = true, bool authorizes = true) : IAuthService
  {
    public ClientConfig? Authenticate (string clientName, string rawKey, DyndnsConfig config)
      => authenticates ? new ClientConfig { Name = clientName, } : null;

    public bool IsRecordAuthorized (ClientConfig client, string zone, string name) => authorizes;
  }

  #endregion

  #region Nested type: StubConfigProvider

  private sealed class StubConfigProvider (Exception? exception = null) : IConfigProvider
  {
    public Task<DyndnsConfig> GetConfigAsync (CancellationToken cancellationToken = default)
      => exception is not null
           ? throw exception
           : Task.FromResult (new DyndnsConfig { Zones = { ["example.com"] = new ZoneConfig { Ttl = 60, }, }, });
  }

  #endregion

  private static DdnsUpdateCoordinator CreateCoordinator (RecordingDnsUpdateService dns,
                                                          bool                      authenticates   = true,
                                                          bool                      authorizes      = true,
                                                          Exception?                configException = null,
                                                          bool                      allowDocumentationAddresses = true)
    => new (configProvider: new StubConfigProvider (configException),
            authService: new StubAuthService (authenticates: authenticates, authorizes: authorizes),
            fqdnResolver: new FqdnResolver (),
            ipResolver: new IpResolver (),
            dnsUpdateService: dns,
            runtimeSettings: Options.Create (new RuntimeSettings { AllowDocumentationAddresses = allowDocumentationAddresses, }),
            logger: NullLogger<DdnsUpdateCoordinator>.Instance);

  private static DdnsUpdateRequest CreateRequest (string? hostname   = Hostname,
                                                  string? explicitIp = "203.0.113.10",
                                                  string  remoteIp   = "203.0.113.99")
  {
    var context = new DefaultHttpContext ();
    context.Connection.RemoteIpAddress = IPAddress.Parse (remoteIp);

    return new DdnsUpdateRequest (ClientName: "client",
                                  RawKey: "key",
                                  Hostname: hostname,
                                  ExplicitIp: explicitIp,
                                  HttpRequest: context.Request);
  }

  [Fact]
  public async Task UpdateAsync_ReturnsSuccess_AndWritesRecord ()
  {
    var                   dns         = new RecordingDnsUpdateService ();
    DdnsUpdateCoordinator coordinator = CreateCoordinator (dns);

    DdnsUpdateResult result = await coordinator.UpdateAsync (CreateRequest ());

    Assert.True (result.IsSuccess);
    Assert.Equal (expected: "A",                              actual: result.Update!.RecordType);
    Assert.Equal (expected: "home.example.com",               actual: result.Update.Fqdn);
    Assert.Equal (expected: IPAddress.Parse ("203.0.113.10"), actual: dns.WrittenAddress);
  }

  [Fact]
  public async Task UpdateAsync_ReportsSuccessWithoutChange_WhenRecordAlreadyCurrent ()
  {
    var dns = new RecordingDnsUpdateService (changed: false);

    DdnsUpdateResult result = await CreateCoordinator (dns).UpdateAsync (CreateRequest ());

    Assert.True (result.IsSuccess);
    Assert.False (result.Update!.Changed);
  }

  [Fact]
  public async Task UpdateAsync_ReturnsConfigurationUnavailable_WhenConfigCannotBeLoaded ()
  {
    var dns = new RecordingDnsUpdateService ();

    DdnsUpdateResult result = await CreateCoordinator (dns: dns, configException: new ConfigurationUnavailableException ("missing"))
                                .UpdateAsync (CreateRequest ());

    Assert.Equal (expected: DdnsUpdateStatus.ConfigurationUnavailable, actual: result.Status);
    Assert.Null (dns.WrittenAddress);
  }

  [Fact]
  public async Task UpdateAsync_ReturnsInvalidCredentials_BeforeAnythingElse ()
  {
    var dns = new RecordingDnsUpdateService ();

    DdnsUpdateResult result = await CreateCoordinator (dns: dns, authenticates: false)
                                .UpdateAsync (CreateRequest (hostname: "nonexistent.invalid"));

    Assert.Equal (expected: DdnsUpdateStatus.InvalidCredentials, actual: result.Status);
    Assert.Null (dns.WrittenAddress);
  }

  [Theory]
  [InlineData (null)]
  [InlineData ("")]
  [InlineData ("unrelated.invalid")]
  public async Task UpdateAsync_ReturnsUnknownHost_WhenHostnameMissingOrOutsideConfiguredZones (string? hostname)
  {
    var dns = new RecordingDnsUpdateService ();

    DdnsUpdateResult result = await CreateCoordinator (dns).UpdateAsync (CreateRequest (hostname: hostname));

    Assert.Equal (expected: DdnsUpdateStatus.UnknownHost, actual: result.Status);
    Assert.Null (dns.WrittenAddress);
  }

  [Fact]
  public async Task UpdateAsync_ReturnsRecordNotAuthorized_WhenClientMayNotUpdateRecord ()
  {
    var dns = new RecordingDnsUpdateService ();

    DdnsUpdateResult result = await CreateCoordinator (dns: dns, authorizes: false).UpdateAsync (CreateRequest ());

    Assert.Equal (expected: DdnsUpdateStatus.RecordNotAuthorized, actual: result.Status);
    Assert.Null (dns.WrittenAddress);
  }

  [Fact]
  public async Task UpdateAsync_ReturnsInvalidAddress_WhenExplicitIpIsNotAnAddress ()
  {
    var dns = new RecordingDnsUpdateService ();

    DdnsUpdateResult result = await CreateCoordinator (dns).UpdateAsync (CreateRequest (explicitIp: "not-an-ip"));

    Assert.Equal (expected: DdnsUpdateStatus.InvalidAddress, actual: result.Status);
    Assert.Null (dns.WrittenAddress);
  }

  [Theory]
  [InlineData ("192.168.1.10")]
  [InlineData ("10.0.0.5")]
  [InlineData ("127.0.0.1")]
  [InlineData ("169.254.3.4")]
  [InlineData ("0.0.0.0")]
  [InlineData ("fd12:3456:789a::1")]
  [InlineData ("fe80::1")]
  [InlineData ("::1")]
  public async Task UpdateAsync_RefusesToPublishNonPublicExplicitAddress (string explicitIp)
  {
    var dns = new RecordingDnsUpdateService ();

    DdnsUpdateResult result = await CreateCoordinator (dns).UpdateAsync (CreateRequest (explicitIp: explicitIp));

    Assert.Equal (expected: DdnsUpdateStatus.AddressNotPubliclyRoutable, actual: result.Status);
    Assert.Null (dns.WrittenAddress);
  }

  [Theory]
  [InlineData ("203.0.113.10")]
  [InlineData ("192.0.2.7")]
  [InlineData ("2001:db8::1")]
  public async Task UpdateAsync_RefusesDocumentationAddresses_WhenTheSettingDisallowsThem (string explicitIp)
  {
    var dns = new RecordingDnsUpdateService ();

    DdnsUpdateResult result = await CreateCoordinator (dns: dns, allowDocumentationAddresses: false)
                                .UpdateAsync (CreateRequest (explicitIp: explicitIp));

    Assert.Equal (expected: DdnsUpdateStatus.AddressNotPubliclyRoutable, actual: result.Status);
    Assert.Null (dns.WrittenAddress);
  }

  [Fact]
  public async Task UpdateAsync_StillPublishesRealAddresses_WhenDocumentationDisallowed ()
  {
    var dns = new RecordingDnsUpdateService ();

    DdnsUpdateResult result = await CreateCoordinator (dns: dns, allowDocumentationAddresses: false)
                                .UpdateAsync (CreateRequest (explicitIp: "8.8.8.8"));

    Assert.True (result.IsSuccess);
    Assert.Equal (expected: IPAddress.Parse ("8.8.8.8"), actual: dns.WrittenAddress);
  }

  [Fact]
  public async Task UpdateAsync_RefusesToPublishAnInternalSourceAddress_WhenNoForwardingHeaderIdentifiesTheCaller ()
  {
    // No explicit IP, and the only visible peer is a private proxy hop with no X-Forwarded-For: the old behavior
    // would have written the proxy's private address to DNS.
    var dns = new RecordingDnsUpdateService ();

    DdnsUpdateResult result = await CreateCoordinator (dns).UpdateAsync (CreateRequest (explicitIp: null, remoteIp: "10.1.2.3"));

    Assert.Equal (expected: DdnsUpdateStatus.AddressNotPubliclyRoutable, actual: result.Status);
    Assert.Null (dns.WrittenAddress);
  }

  [Theory]
  [InlineData ("203.0.113.10")]   // documentation range: allowed (the smoke test uses these)
  [InlineData ("2001:db8::1")]
  [InlineData ("8.8.8.8")]
  [InlineData ("2a02:1234:5678::5")]
  public async Task UpdateAsync_PublishesPubliclyRoutableAndDocumentationAddresses (string explicitIp)
  {
    var dns = new RecordingDnsUpdateService ();

    DdnsUpdateResult result = await CreateCoordinator (dns).UpdateAsync (CreateRequest (explicitIp: explicitIp));

    Assert.True (result.IsSuccess);
    Assert.Equal (expected: IPAddress.Parse (explicitIp), actual: dns.WrittenAddress);
  }

  [Fact]
  public async Task UpdateAsync_FallsBackToSourceIp_WhenNoExplicitIp ()
  {
    var dns = new RecordingDnsUpdateService ();

    DdnsUpdateResult result = await CreateCoordinator (dns).UpdateAsync (CreateRequest (explicitIp: null));

    Assert.True (result.IsSuccess);
    Assert.Equal (expected: IPAddress.Parse ("203.0.113.99"), actual: dns.WrittenAddress);
  }

  [Fact]
  public async Task UpdateAsync_WritesIpv4Record_WhenExplicitIpIsIpv4MappedIpv6 ()
  {
    var dns = new RecordingDnsUpdateService ();

    DdnsUpdateResult result = await CreateCoordinator (dns).UpdateAsync (CreateRequest (explicitIp: "::ffff:203.0.113.5"));

    Assert.True (result.IsSuccess);
    Assert.Equal (expected: "A",                             actual: result.Update!.RecordType);
    Assert.Equal (expected: IPAddress.Parse ("203.0.113.5"), actual: dns.WrittenAddress);
  }

  [Fact]
  public async Task UpdateAsync_WritesIpv4Record_WhenSourceIpIsIpv4MappedIpv6 ()
  {
    var dns = new RecordingDnsUpdateService ();

    DdnsUpdateResult result = await CreateCoordinator (dns)
                                .UpdateAsync (CreateRequest (explicitIp: null, remoteIp: "::ffff:203.0.113.77"));

    Assert.True (result.IsSuccess);
    Assert.Equal (expected: "A",                              actual: result.Update!.RecordType);
    Assert.Equal (expected: IPAddress.Parse ("203.0.113.77"), actual: dns.WrittenAddress);
  }

  [Theory]
  [MemberData (nameof (DnsFailures))]
  public async Task UpdateAsync_ReportsDnsWriteFailuresAsStatuses_NeverThrows (Exception failure, DdnsUpdateStatus expected)
  {
    var dns = new RecordingDnsUpdateService (failure);

    DdnsUpdateResult result = await CreateCoordinator (dns).UpdateAsync (CreateRequest ());

    Assert.Equal (expected: expected, actual: result.Status);
    Assert.Null (result.Update);
  }
}
