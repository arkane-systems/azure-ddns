#region header

// AzureDdns.FunctionApp - DnsRecordComparison.cs
//
// Alistair J. R. Young
// Arkane Systems
//
// Copyright Arkane Systems 2012-2018.  All rights reserved.
//
// Created: 2026-10-06 4:00 PM

#endregion

#region using

using System.Net;

#endregion

namespace AzureDdns.FunctionApp.Services;

/// <summary>
///   Decides whether an existing DNS record set already holds exactly what an update would write, so the update
///   can be skipped.
/// </summary>
/// <remarks>
///   Kept separate from <see cref="DnsUpdateService" /> because it is pure logic (no Azure calls) and therefore
///   unit-testable. An update replaces the whole record set, so "already current" means the set contains exactly
///   the one desired address with exactly the desired TTL; anything else (extra addresses, a different TTL, an
///   empty or alias record set) must be rewritten.
/// </remarks>
public static class DnsRecordComparison
{
  /// <summary>
  ///   Returns <see langword="true" /> when writing the desired address and TTL would change nothing.
  /// </summary>
  /// <param name="existingAddresses">Addresses currently in the record set (entries may be <see langword="null" />).</param>
  /// <param name="existingTtl">TTL currently on the record set, if any.</param>
  /// <param name="desiredAddress">The address the update would write.</param>
  /// <param name="desiredTtl">The TTL the update would write.</param>
  public static bool IsCurrent (IEnumerable<IPAddress?> existingAddresses,
                                long?                   existingTtl,
                                IPAddress               desiredAddress,
                                long                    desiredTtl)
  {
    ArgumentNullException.ThrowIfNull (existingAddresses);
    ArgumentNullException.ThrowIfNull (desiredAddress);

    if (existingTtl != desiredTtl)
      return false;

    IPAddress?[] addresses = existingAddresses.ToArray ();

    return addresses.Length == 1 && addresses[0] is not null && addresses[0]!.Equals (desiredAddress);
  }
}
