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
    /// <summary>
    ///     Authenticates a client request using client name and raw key material.
    /// </summary>
    /// <param name="clientName">Client name supplied by the DDNS caller.</param>
    /// <param name="rawKey">Raw key supplied by the DDNS caller.</param>
    /// <param name="config">Current DDNS configuration snapshot.</param>
    /// <returns>
    ///     The authenticated client configuration when credentials are valid; otherwise <see langword="null"/>.
    /// </returns>
    ClientConfig? Authenticate (string clientName, string rawKey, DyndnsConfig config);

    /// <summary>
    ///     Checks whether an authenticated client may update a requested zone/record pair.
    /// </summary>
    /// <param name="client">Authenticated client configuration.</param>
    /// <param name="zone">Requested DNS zone.</param>
    /// <param name="name">Requested record name.</param>
    /// <returns><see langword="true"/> when the record is explicitly allowed; otherwise <see langword="false"/>.</returns>
    bool IsRecordAuthorized (ClientConfig client, string zone, string name);
}

public sealed class AuthService : IAuthService
{
    /// <summary>
    ///     Validates a client name/key pair against configured SHA-256 hashes.
    /// </summary>
    /// <remarks>
    ///     Key comparison uses fixed-time byte comparison to reduce timing side-channel risk.
    /// </remarks>
    public ClientConfig? Authenticate (string clientName, string rawKey, DyndnsConfig config)
    {
        if (string.IsNullOrWhiteSpace (clientName) || string.IsNullOrWhiteSpace (rawKey))
            return null;

        ClientConfig? client =
            config.Clients.FirstOrDefault (candidate => string.Equals (a: candidate.Name,
                                                                       b: clientName,
                                                                       comparisonType: StringComparison.OrdinalIgnoreCase));

        if (client is null || string.IsNullOrWhiteSpace (client.KeyHash))
            return null;

        string providedHash = ComputeSha256 (rawKey);

        return CryptographicOperations.FixedTimeEquals (left: Encoding.UTF8.GetBytes (providedHash),
                                                        right: Encoding.UTF8.GetBytes (client.KeyHash.Trim ().ToLowerInvariant ()))
                   ? client
                   : null;
    }

    /// <summary>
    ///     Validates whether a client may update a specific record in a specific zone.
    /// </summary>
    /// <remarks>
    ///     Wildcard authorization is supported only at record-name level (<c>*</c>) within an allowed zone.
    /// </remarks>
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

    /// <summary>
    ///     Computes a lowercase hexadecimal SHA-256 hash for a secret value.
    /// </summary>
    /// <param name="value">Raw secret/key value.</param>
    /// <returns>Lowercase SHA-256 hex digest.</returns>
    public static string ComputeSha256 (string value)
    {
        byte[] bytes = SHA256.HashData (Encoding.UTF8.GetBytes (value));

        return Convert.ToHexString (bytes).ToLowerInvariant ();
    }
}
