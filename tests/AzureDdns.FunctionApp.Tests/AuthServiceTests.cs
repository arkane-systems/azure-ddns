#region header

// AzureDdns.FunctionApp.Tests - AuthServiceTests.cs
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
using AzureDdns.FunctionApp.Services;

#endregion

namespace AzureDdns.FunctionApp.Tests;

public sealed class AuthServiceTests
{
    private readonly AuthService _authService = new ();

    [Fact]
    public void Authenticate_ReturnsClient_ForValidNameAndKey ()
    {
        var config = new DyndnsConfig
        {
            Clients =
                         [
                             new ClientConfig
                             {
                                 Name           = "home-router",
                                 KeyHash        = AuthService.ComputeSha256 ("secret-key"),
                                 AllowedRecords = [new AllowedRecordConfig { Zone = "example.com", Name = "home", },],
                             },
                         ],
        };

        ClientConfig? result = this._authService.Authenticate (clientName: "home-router", rawKey: "secret-key", config: config);

        Assert.NotNull (result);
        Assert.Equal (expected: "home-router", actual: result.Name);
    }

    [Fact]
    public void Authenticate_ReturnsNull_ForInvalidKey ()
    {
        var config = new DyndnsConfig
        {
            Clients =
                         [
                             new ClientConfig { Name = "home-router", KeyHash = AuthService.ComputeSha256 ("secret-key"), },
                         ],
        };

        ClientConfig? result = this._authService.Authenticate (clientName: "home-router", rawKey: "wrong-key", config: config);

        Assert.Null (result);
    }

    [Fact]
    public void IsRecordAuthorized_ReturnsTrue_ForWildcardName ()
    {
        var client = new ClientConfig
        {
            Name = "home-router",
            AllowedRecords = [new AllowedRecordConfig { Zone = "example.com", Name = "*", },],
        };

        bool result = this._authService.IsRecordAuthorized (client: client, zone: "example.com", name: "kitchen");

        Assert.True (result);
    }

    [Fact]
    public void IsRecordAuthorized_ReturnsFalse_ForDifferentZone ()
    {
        var client = new ClientConfig
        {
            Name = "home-router",
            AllowedRecords = [new AllowedRecordConfig { Zone = "example.com", Name = "home", },],
        };

        bool result = this._authService.IsRecordAuthorized (client: client, zone: "other.com", name: "home");

        Assert.False (result);
    }

    [Theory]
    [InlineData ("example.com.",  "example.com")]
    [InlineData ("example.com",   "example.com.")]
    [InlineData ("EXAMPLE.com",   "example.com")]
    [InlineData (" example.com ", "example.com")]
    public void IsRecordAuthorized_IgnoresZoneFormattingDifferences (string allowedZone, string requestedZone)
    {
        var client = new ClientConfig
        {
            Name = "home-router",
            AllowedRecords = [new AllowedRecordConfig { Zone = allowedZone, Name = "home", },],
        };

        bool result = this._authService.IsRecordAuthorized (client: client, zone: requestedZone, name: "home");

        Assert.True (result);
    }

    [Fact]
    public void Authenticate_ReturnsNull_ForUnknownClient ()
    {
        var config = new DyndnsConfig
        {
            Clients = [new ClientConfig { Name = "home-router", KeyHash = AuthService.ComputeSha256 ("secret-key"), },],
        };

        ClientConfig? result = this._authService.Authenticate (clientName: "no-such-client", rawKey: "secret-key", config: config);

        Assert.Null (result);
    }

    [Fact]
    public void Authenticate_ReturnsNull_ForNoConfiguredClients ()
    {
        ClientConfig? result = this._authService.Authenticate (clientName: "home-router", rawKey: "secret-key", config: new DyndnsConfig ());

        Assert.Null (result);
    }

    [Theory]
    [InlineData ("")]
    [InlineData ("   ")]
    public void Authenticate_ReturnsNull_WhenClientHasBlankHash (string keyHash)
    {
        var config = new DyndnsConfig
        {
            Clients = [new ClientConfig { Name = "home-router", KeyHash = keyHash, },],
        };

        ClientConfig? result = this._authService.Authenticate (clientName: "home-router", rawKey: "secret-key", config: config);

        Assert.Null (result);
    }

    [Fact]
    public void Authenticate_ReturnsNull_WhenKeyIsTheDummyComparisonValue ()
    {
        // The all-zero hash is only ever a comparison placeholder; no key hashes to it and it must never authenticate.
        var config = new DyndnsConfig
        {
            Clients = [new ClientConfig { Name = "home-router", KeyHash = new string ('0', 64), },],
        };

        ClientConfig? result = this._authService.Authenticate (clientName: "home-router", rawKey: "anything", config: config);

        Assert.Null (result);
    }

    [Fact]
    public void Authenticate_MatchesClientNameAndHashFormattingLeniently ()
    {
        var config = new DyndnsConfig
        {
            Clients = [new ClientConfig { Name = "Home-Router", KeyHash = "  " + AuthService.ComputeSha256 ("secret-key").ToUpperInvariant () + " ", },],
        };

        ClientConfig? result = this._authService.Authenticate (clientName: "home-ROUTER", rawKey: "secret-key", config: config);

        Assert.NotNull (result);
    }

    [Fact]
    public void Authenticate_ReturnsTheFirstClientWithAMatchingName_WhenNamesRepeat ()
    {
        var config = new DyndnsConfig
        {
            Clients =
            [
                new ClientConfig { Name = "home-router", KeyHash = AuthService.ComputeSha256 ("first-key"), },
                new ClientConfig { Name = "home-router", KeyHash = AuthService.ComputeSha256 ("second-key"), },
            ],
        };

        Assert.NotNull (this._authService.Authenticate (clientName: "home-router", rawKey: "first-key", config: config));
        Assert.Null (this._authService.Authenticate (clientName: "home-router", rawKey: "second-key", config: config));
    }
}
