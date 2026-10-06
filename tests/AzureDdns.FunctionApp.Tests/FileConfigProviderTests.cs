#region header

// AzureDdns.FunctionApp.Tests - FileConfigProviderTests.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-04-18 12:00 AM

#endregion

#region using

using AzureDdns.FunctionApp.Config;
using AzureDdns.FunctionApp.Services;

using Microsoft.Extensions.Options;

#endregion

namespace AzureDdns.FunctionApp.Tests;

public sealed class FileConfigProviderTests : IDisposable
{
  private readonly string directory = Directory.CreateTempSubdirectory ("azure-ddns-tests-").FullName;

  public void Dispose () => Directory.Delete (path: this.directory, recursive: true);

  private FileConfigProvider CreateProvider (string fileName)
    => new (Options.Create (new RuntimeSettings { ConfigPath = Path.Combine (path1: this.directory, path2: fileName), }));

  [Fact]
  public async Task GetConfigAsync_ThrowsConfigurationUnavailable_WhenFileMissing ()
  {
    FileConfigProvider provider = CreateProvider ("missing.json");

    await Assert.ThrowsAsync<ConfigurationUnavailableException> (() => provider.GetConfigAsync ());
  }

  [Theory]
  [InlineData ("{ not json")]
  [InlineData ("null")]
  [InlineData ("")]
  public async Task GetConfigAsync_ThrowsConfigurationUnavailable_WhenFileMalformed (string content)
  {
    await File.WriteAllTextAsync (path: Path.Combine (path1: this.directory, path2: "bad.json"), contents: content);
    FileConfigProvider provider = CreateProvider ("bad.json");

    await Assert.ThrowsAsync<ConfigurationUnavailableException> (() => provider.GetConfigAsync ());
  }

  [Fact]
  public async Task GetConfigAsync_ReturnsConfig_WhenFileValid ()
  {
    const string json = """
                        { "zones": { "example.com": { "ttl": 60 } },
                          "clients": [ { "name": "c", "keyHash": "abc", "allowedRecords": [ { "zone": "example.com", "name": "home" } ] } ] }
                        """;
    await File.WriteAllTextAsync (path: Path.Combine (path1: this.directory, path2: "ok.json"), contents: json);
    FileConfigProvider provider = CreateProvider ("ok.json");

    DyndnsConfig config = await provider.GetConfigAsync ();

    Assert.Equal (expected: 60, actual: config.Zones["example.com"].Ttl);
    Assert.Single (config.Clients);
  }

  [Fact]
  public async Task GetConfigAsync_ReturnsEmptyConfig_WhenFileIsValidButEmptyObject ()
  {
    await File.WriteAllTextAsync (path: Path.Combine (path1: this.directory, path2: "empty.json"), contents: "{}");
    FileConfigProvider provider = CreateProvider ("empty.json");

    DyndnsConfig config = await provider.GetConfigAsync ();

    Assert.Empty (config.Zones);
    Assert.Empty (config.Clients);
  }
}
