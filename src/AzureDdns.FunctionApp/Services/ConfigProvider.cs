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
  /// <exception cref="ConfigurationUnavailableException">
  ///   The configuration could not be loaded (file missing, unreadable, or malformed).
  /// </exception>
  Task<DyndnsConfig> GetConfigAsync (CancellationToken cancellationToken = default);
}

/// <summary>
///   Thrown when the DDNS configuration cannot be loaded (missing, unreadable, or malformed file).
///   This is a server-side fault distinct from a valid configuration that simply denies the request,
///   so the function layer reports it as "service unavailable" rather than an authentication failure.
/// </summary>
/// <remarks>
///   The message names the file path and the failure reason but never includes file content.
/// </remarks>
public sealed class ConfigurationUnavailableException (string message, Exception? innerException = null)
  : Exception (message: message, innerException: innerException);

/// <summary>
///   Loads DDNS configuration from a JSON file path defined by runtime settings.
/// </summary>
/// <remarks>
///   This provider intentionally favors operational simplicity over dynamic refresh complexity.
///   A missing, unreadable, or malformed file raises <see cref="ConfigurationUnavailableException" />
///   (reported as HTTP 503 by the functions) rather than silently behaving like an empty configuration,
///   which would make every client look like it had bad credentials. A well-formed file that simply
///   lists no clients or zones is valid and is returned as-is.
/// </remarks>
public sealed class FileConfigProvider (IOptions<RuntimeSettings> settings) : IConfigProvider
{
  private static readonly JsonSerializerOptions SerializerOptions = new (JsonSerializerDefaults.Web);

  private readonly RuntimeSettings settings = settings.Value;

  /// <summary>
  ///   Loads and deserializes DDNS configuration from the configured file path.
  /// </summary>
  /// <param name="cancellationToken">Cancellation token for file stream read/deserialization.</param>
  /// <returns>Loaded configuration.</returns>
  /// <exception cref="ConfigurationUnavailableException">The file does not exist, cannot be read, or cannot be parsed.</exception>
  public async Task<DyndnsConfig> GetConfigAsync (CancellationToken cancellationToken = default)
  {
    // Relative paths are resolved from app base directory so packaged config works in Azure and local runs.
    string fullPath = Path.IsPathRooted (this.settings.ConfigPath)
                        ? this.settings.ConfigPath
                        : Path.Combine (path1: AppContext.BaseDirectory, path2: this.settings.ConfigPath);

    if (!File.Exists (fullPath))
      throw new ConfigurationUnavailableException ($"Configuration file not found: {fullPath}");

    try
    {
      await using FileStream stream = File.OpenRead (fullPath);

      return await JsonSerializer.DeserializeAsync<DyndnsConfig> (utf8Json: stream,
                                                                   options: SerializerOptions,
                                                                   cancellationToken: cancellationToken) ??
             throw new ConfigurationUnavailableException ($"Configuration file is empty or null: {fullPath}");
    }
    catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
    {
      // Surface as a distinct service-unavailable condition. Only the path is included in the message;
      // the inner exception is kept for diagnostics, but config content is never logged.
      throw new ConfigurationUnavailableException (message: $"Configuration file could not be read or parsed: {fullPath}",
                                                   innerException: exception);
    }
  }
}
