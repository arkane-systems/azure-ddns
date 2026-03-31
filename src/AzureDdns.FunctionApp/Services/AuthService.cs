#region header

// AzureDdns.FunctionApp - AuthService.cs
// 
// Alistair J. R. Young
// Arkane Systems
// 
// Copyright Arkane Systems 2012-2018.  All rights reserved.
// 
// Created: 2026-03-30 7:41 PM

#endregion

#region using

using AzureDdns.FunctionApp.Config;
using System.Security.Cryptography;
using System.Text;

#endregion

namespace AzureDdns.FunctionApp.Services;

public interface IAuthService
{
    ClientConfig? Authenticate (string clientName, string rawKey, DyndnsConfig config);

    bool IsRecordAuthorized (ClientConfig client, string zone, string name);
}

public sealed class AuthService : IAuthService
{
    public ClientConfig? Authenticate (string clientName, string rawKey, DyndnsConfig config)
    {
        if (string.IsNullOrWhiteSpace (clientName) || string.IsNullOrWhiteSpace (rawKey))
            return null;

        ClientConfig? client =
            config.Clients.FirstOrDefault (candidate => string.Equals (a: candidate.Name,
                                                                       b: clientName,
                                                                       comparisonType: StringComparison.OrdinalIgnoreCase)) ;

        if (client is null || string.IsNullOrWhiteSpace (client.KeyHash))
            return null;

        string providedHash = ComputeSha256 (rawKey) ;

        return CryptographicOperations.FixedTimeEquals (left: Encoding.UTF8.GetBytes (providedHash),
                                                        right: Encoding.UTF8.GetBytes (client.KeyHash.Trim ().ToLowerInvariant ()))
                   ? client
                   : null;
    }

    public bool IsRecordAuthorized (ClientConfig client, string zone, string name)
    {
        if (string.IsNullOrWhiteSpace (zone) || string.IsNullOrWhiteSpace (name))
            return false;

        return client.AllowedRecords.Any (record =>
                                              string.Equals (a: record.Zone,
                                                             b: zone,
                                                             comparisonType: StringComparison.OrdinalIgnoreCase) &&
                                              (string.Equals (a: record.Name,
                                                              b: name,
                                                              comparisonType: StringComparison.OrdinalIgnoreCase) ||
                                               string.Equals (a: record.Name, b: "*", comparisonType: StringComparison.Ordinal)));
    }

    public static string ComputeSha256 (string value)
    {
        byte[] bytes = SHA256.HashData (Encoding.UTF8.GetBytes (value)) ;

        return Convert.ToHexString (bytes).ToLowerInvariant ();
    }
}
