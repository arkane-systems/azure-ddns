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

using System.Security.Cryptography;
using System.Text;

using AzureDdns.FunctionApp.Config;

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
    ///     The authenticated client configuration when credentials are valid; otherwise <see langword="null" />.
    /// </returns>
    ClientConfig? Authenticate (string clientName, string rawKey, DyndnsConfig config);

    /// <summary>
    ///     Checks whether an authenticated client may update a requested zone/record pair.
    /// </summary>
    /// <param name="client">Authenticated client configuration.</param>
    /// <param name="zone">Requested DNS zone.</param>
    /// <param name="name">Requested record name.</param>
    /// <returns><see langword="true" /> when the record is explicitly allowed; otherwise <see langword="false" />.</returns>
    bool IsRecordAuthorized (ClientConfig client, string zone, string name);
}

public sealed class AuthService : IAuthService
{
    /// <summary>
    ///     A well-formed (64 hex character) hash compared against when there is no real hash to compare with, so that
    ///     the comparison still happens. It is never accepted as a match; see <see cref="Authenticate" />.
    /// </summary>
    private const string DummyHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    ///     Validates a client name/key pair against configured SHA-256 hashes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Key comparison uses fixed-time byte comparison to reduce timing side-channel risk.
    ///     </para>
    ///     <para>
    ///         The same work is done whether or not the client name exists (or has a usable hash): the provided
    ///         key is always hashed, and always compared in fixed time (against <see cref="DummyHash" /> when
    ///         there is nothing real to compare with), and the client list is always scanned in full. Without
    ///         this, an unknown client name would return measurably sooner than a known one, letting a caller
    ///         discover which client names exist. The comparison against the dummy can never authenticate:
    ///         success additionally requires a matching client with a non-blank hash.
    ///     </para>
    /// </remarks>
    public ClientConfig? Authenticate (string clientName, string rawKey, DyndnsConfig config)
    {
        if (string.IsNullOrWhiteSpace (clientName) || string.IsNullOrWhiteSpace (rawKey))
            return null;

        // Scan every client (no early exit) so lookup time does not depend on where, or whether, the name matches.
        ClientConfig? client = null;

        foreach (ClientConfig candidate in config.Clients)
        {
            if (client is null &&
                string.Equals (a: candidate.Name, b: clientName, comparisonType: StringComparison.OrdinalIgnoreCase))
                client = candidate;
        }

        bool   hasUsableHash = client is not null && !string.IsNullOrWhiteSpace (client.KeyHash);
        string providedHash  = ComputeSha256 (rawKey);
        // Normalize in both cases so the string work is identical on every path.
        string expectedHash  = (hasUsableHash ? client!.KeyHash : DummyHash).Trim ().ToLowerInvariant ();

        bool hashesMatch = CryptographicOperations.FixedTimeEquals (left: Encoding.UTF8.GetBytes (providedHash),
                                                                    right: Encoding.UTF8.GetBytes (expectedHash));

        return hasUsableHash && hashesMatch ? client : null;
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

        // Zones are compared in normalized form (see DyndnsConfig.NormalizeZoneName) so that
        // formatting differences such as a trailing dot do not cause spurious denials.
        string normalizedZone = DyndnsConfig.NormalizeZoneName (zone);

        return client.AllowedRecords.Any (record =>
                                              string.Equals (a: DyndnsConfig.NormalizeZoneName (record.Zone),
                                                             b: normalizedZone,
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
