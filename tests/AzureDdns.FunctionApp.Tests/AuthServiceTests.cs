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
    private readonly AuthService _authService = new () ;

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
        } ;

        ClientConfig? result = this._authService.Authenticate (clientName: "home-router", rawKey: "secret-key", config: config) ;

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
        } ;

        ClientConfig? result = this._authService.Authenticate (clientName: "home-router", rawKey: "wrong-key", config: config) ;

        Assert.Null (result);
    }

    [Fact]
    public void IsRecordAuthorized_ReturnsTrue_ForWildcardName ()
    {
        var client = new ClientConfig
        {
            Name = "home-router",
            AllowedRecords = [new AllowedRecordConfig { Zone = "example.com", Name = "*", },],
        } ;

        bool result = this._authService.IsRecordAuthorized (client: client, zone: "example.com", name: "kitchen") ;

        Assert.True (result);
    }

    [Fact]
    public void IsRecordAuthorized_ReturnsFalse_ForDifferentZone ()
    {
        var client = new ClientConfig
        {
            Name = "home-router",
            AllowedRecords = [new AllowedRecordConfig { Zone = "example.com", Name = "home", },],
        } ;

        bool result = this._authService.IsRecordAuthorized (client: client, zone: "other.com", name: "home") ;

        Assert.False (result);
    }
}
