using System.Security.Cryptography;
using System.Text;
using AzureDdns.FunctionApp.Config;

namespace AzureDdns.FunctionApp.Services;

public interface IAuthService
{
    ClientConfig? Authenticate(string clientName, string rawKey, DyndnsConfig config);

    bool IsRecordAuthorized(ClientConfig client, string zone, string name);
}

public sealed class AuthService : IAuthService
{
    public ClientConfig? Authenticate(string clientName, string rawKey, DyndnsConfig config)
    {
        var client = config.Clients.FirstOrDefault(candidate => string.Equals(candidate.Name, clientName, StringComparison.OrdinalIgnoreCase));
        if (client is null)
        {
            return null;
        }

        var providedHash = ComputeSha256(rawKey);
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(providedHash), Encoding.UTF8.GetBytes(client.KeyHash))
            ? client
            : null;
    }

    public bool IsRecordAuthorized(ClientConfig client, string zone, string name)
    {
        return client.AllowedRecords.Any(record =>
            string.Equals(record.Zone, zone, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(record.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(record.Name, "*", StringComparison.Ordinal)));
    }

    public static string ComputeSha256(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
