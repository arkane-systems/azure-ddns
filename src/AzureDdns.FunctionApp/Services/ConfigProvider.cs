#region header

// AzureDdns.FunctionApp - ConfigProvider.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-03-30 10:16 PM

#endregion

#region using

using System.Text.Json;

using AzureDdns.FunctionApp.Config;

using Microsoft.Extensions.Options;

#endregion

namespace AzureDdns.FunctionApp.Services;

public interface IConfigProvider
{
  /// <summary>
  ///   Retrieves the current DDNS configuration snapshot used for request authentication/authorization.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token for asynchronous I/O.</param>
  /// <returns>Deserialized configuration; never <see langword="null" />.</returns>
  Task<DyndnsConfig> GetConfigAsync (CancellationToken cancellationToken = default);
}

/// <summary>
///   Loads DDNS configuration from a JSON file path defined by runtime settings.
/// </summary>
/// <remarks>
///   This provider intentionally favors operational simplicity over dynamic refresh complexity.
///   Missing/invalid files resolve to an empty configuration object to keep failure behavior explicit
///   in the function layer (for example, returning "zone not configured").
/// </remarks>
public sealed class FileConfigProvider (IOptions<RuntimeSettings> settings) : IConfigProvider
{
  private static readonly JsonSerializerOptions SerializerOptions = new (JsonSerializerDefaults.Web);

  private readonly RuntimeSettings settings = settings.Value;

  /// <summary>
  ///   Loads and deserializes DDNS configuration from the configured file path.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token for file stream read/deserialization.</param>
  /// <returns>Loaded configuration, or empty configuration when file does not exist.</returns>
  public async Task<DyndnsConfig> GetConfigAsync (CancellationToken cancellationToken = default)
  {
    // Relative paths are resolved from app base directory so packaged config works in Azure and local runs.
    string fullPath = Path.IsPathRooted (this.settings.ConfigPath)
                        ? this.settings.ConfigPath
                        : Path.Combine (path1: AppContext.BaseDirectory, path2: this.settings.ConfigPath);

    if (!File.Exists (fullPath))
      return new DyndnsConfig ();

    await using FileStream stream = File.OpenRead (fullPath);

    return await JsonSerializer.DeserializeAsync<DyndnsConfig> (utf8Json: stream,
                                                                options: SerializerOptions,
                                                                cancellationToken: cancellationToken) ??
           new DyndnsConfig ();
  }
}
