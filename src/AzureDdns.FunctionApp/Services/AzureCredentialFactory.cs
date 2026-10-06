#region header

// AzureDdns.FunctionApp - AzureCredentialFactory.cs
//
// Alistair J. R. Young
// Arkane Systems
//
// Copyright Arkane Systems 2012-2018.  All rights reserved.
//
// Created: 2026-10-06 7:00 PM

#endregion

#region using

using Azure.Core;
using Azure.Identity;

#endregion

namespace AzureDdns.FunctionApp.Services;

/// <summary>
///   Chooses the credential used to call Azure Resource Manager (Azure DNS).
/// </summary>
/// <remarks>
///   <para>
///     In Azure the app authenticates as its managed identity, and only that: <see cref="ManagedIdentityCredential" />
///     is used directly rather than <see cref="DefaultAzureCredential" />. The default chain probes several credential
///     sources in turn (environment variables, workload identity, managed identity, and several developer-tool
///     sources). In a hosted app that is wasted work on a cold start, it can pick up an unintended source (for example a
///     stray <c>AZURE_CLIENT_SECRET</c>), and when the identity is missing it reports a long aggregated failure instead
///     of one clear "managed identity unavailable" error.
///   </para>
///   <para>
///     Off Azure (local development, tests) there is no managed identity, so <see cref="DefaultAzureCredential" /> is
///     used and picks up the developer's <c>az login</c> or similar.
///   </para>
///   <para>
///     If detection ever wrongly concludes "not in Azure", the result is merely the slower default chain, which still
///     includes the managed identity; it cannot make a working deployment fail.
///   </para>
/// </remarks>
public static class AzureCredentialFactory
{
  /// <summary>
  ///   Environment variables the platform sets only when a managed identity is available: the address of its token
  ///   endpoint. Deliberately these and not general "hosted on App Service" markers such as
  ///   <c>WEBSITE_SITE_NAME</c>, which tooling (Functions Core Tools, emulators) may also set on a developer
  ///   machine, where the managed identity does not exist.
  /// </summary>
  private static readonly string[] HostedInAzureVariables =
  [
    "IDENTITY_ENDPOINT", // managed identity token endpoint (current)
    "MSI_ENDPOINT",      // managed identity token endpoint (legacy)
  ];

  /// <summary>
  ///   Creates the credential appropriate to where the app is running.
  /// </summary>
  /// <param name="getEnvironmentVariable">Environment lookup; defaults to the process environment (injectable for tests).</param>
  /// <remarks>
  ///   A user-assigned identity is selected by setting <c>AZURE_CLIENT_ID</c> (the standard variable) to its client ID;
  ///   otherwise the system-assigned identity is used, which is what <c>infra/main.bicep</c> creates.
  /// </remarks>
  public static TokenCredential Create (Func<string, string?>? getEnvironmentVariable = null)
  {
    getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

    if (!IsHostedInAzure (getEnvironmentVariable))
      return new DefaultAzureCredential ();

    string? clientId = getEnvironmentVariable ("AZURE_CLIENT_ID");

    return new ManagedIdentityCredential (string.IsNullOrWhiteSpace (clientId)
                                            ? ManagedIdentityId.SystemAssigned
                                            : ManagedIdentityId.FromUserAssignedClientId (clientId.Trim ()));
  }

  /// <summary>
  ///   Whether the environment looks like an Azure-hosted app (as opposed to a developer machine).
  /// </summary>
  public static bool IsHostedInAzure (Func<string, string?> getEnvironmentVariable)
  {
    ArgumentNullException.ThrowIfNull (getEnvironmentVariable);

    return HostedInAzureVariables.Any (name => !string.IsNullOrWhiteSpace (getEnvironmentVariable (name)));
  }
}
