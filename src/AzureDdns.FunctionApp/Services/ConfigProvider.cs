using System.Text.Json;
using AzureDdns.FunctionApp.Config;
using Microsoft.Extensions.Options;

namespace AzureDdns.FunctionApp.Services;

public interface IConfigProvider
{
    Task<DyndnsConfig> GetConfigAsync(CancellationToken cancellationToken = default);
}

public sealed class FileConfigProvider : IConfigProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly RuntimeSettings _settings;

    public FileConfigProvider(IOptions<RuntimeSettings> settings)
    {
        _settings = settings.Value;
    }

    public async Task<DyndnsConfig> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        var fullPath = Path.IsPathRooted(_settings.ConfigPath)
            ? _settings.ConfigPath
            : Path.Combine(AppContext.BaseDirectory, _settings.ConfigPath);

        if (!File.Exists(fullPath))
        {
            return new DyndnsConfig();
        }

        await using var stream = File.OpenRead(fullPath);
        return await JsonSerializer.DeserializeAsync<DyndnsConfig>(stream, SerializerOptions, cancellationToken)
            ?? new DyndnsConfig();
    }
}
