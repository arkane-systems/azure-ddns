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

using AzureDdns.FunctionApp.Config;
using Microsoft.Extensions.Options;
using System.Text.Json;

#endregion

namespace AzureDdns.FunctionApp.Services;

public interface IConfigProvider
{
    Task<DyndnsConfig> GetConfigAsync (CancellationToken cancellationToken = default);
}

public sealed class FileConfigProvider : IConfigProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new (JsonSerializerDefaults.Web);

    public FileConfigProvider (IOptions<RuntimeSettings> settings) => this._settings = settings.Value;

    private readonly RuntimeSettings _settings;

    public async Task<DyndnsConfig> GetConfigAsync (CancellationToken cancellationToken = default)
    {
        string fullPath = Path.IsPathRooted (this._settings.ConfigPath)
                              ? this._settings.ConfigPath
                              : Path.Combine (path1: AppContext.BaseDirectory, path2: this._settings.ConfigPath);

        if (!File.Exists (fullPath))
            return new DyndnsConfig ();

        await using FileStream stream = File.OpenRead (fullPath);

        return await JsonSerializer.DeserializeAsync<DyndnsConfig> (utf8Json: stream,
                                                                     options: SerializerOptions,
                                                                     cancellationToken: cancellationToken) ??
               new DyndnsConfig ();
    }
}
