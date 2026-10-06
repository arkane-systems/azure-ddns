#region header

// AzureDdns.FunctionApp.Tests - AzureCredentialFactoryTests.cs
//
// Alistair J. R. Young
// Arkane Systems
//
// Copyright Arkane Systems 2012-2018.  All rights reserved.
//
// Created: 2026-10-06 7:00 PM

#endregion

#region using

using Azure.Identity;

using AzureDdns.FunctionApp.Services;

#endregion

namespace AzureDdns.FunctionApp.Tests;

public sealed class AzureCredentialFactoryTests
{
  private static Func<string, string?> Env (params (string Name, string Value)[] variables)
  {
    var map = variables.ToDictionary (keySelector: v => v.Name, elementSelector: v => v.Value, comparer: StringComparer.Ordinal);

    return name => map.GetValueOrDefault (name);
  }

  [Fact]
  public void Create_UsesDefaultChain_OffAzure ()
    => Assert.IsType<DefaultAzureCredential> (AzureCredentialFactory.Create (Env ()));

  [Theory]
  [InlineData ("IDENTITY_ENDPOINT", "http://127.0.0.1:41337/msi/token")]
  [InlineData ("MSI_ENDPOINT", "http://127.0.0.1:41337/MSI/token/")]
  public void Create_UsesManagedIdentityDirectly_InAzure (string variable, string value)
    => Assert.IsType<ManagedIdentityCredential> (AzureCredentialFactory.Create (Env ((variable, value))));

  [Fact]
  public void Create_UsesUserAssignedManagedIdentity_WhenClientIdIsSet ()
    => Assert.IsType<ManagedIdentityCredential> (AzureCredentialFactory.Create (Env (("IDENTITY_ENDPOINT", "http://127.0.0.1/msi"),
                                                                                     ("AZURE_CLIENT_ID", "11111111-2222-3333-4444-555555555555"))));

  [Fact]
  public void Create_IgnoresBlankClientId_AndStillUsesManagedIdentity ()
    => Assert.IsType<ManagedIdentityCredential> (AzureCredentialFactory.Create (Env (("IDENTITY_ENDPOINT", "http://127.0.0.1/msi"),
                                                                                     ("AZURE_CLIENT_ID", "   "))));

  [Fact]
  public void Create_AppServiceMarkersAloneDoNotMeanAManagedIdentityExists ()
    => Assert.IsType<DefaultAzureCredential> (AzureCredentialFactory.Create (Env (("WEBSITE_SITE_NAME", "local-func-host"))));

  [Fact]
  public void Create_ClientIdAloneDoesNotMeanHostedInAzure ()
    => Assert.IsType<DefaultAzureCredential> (AzureCredentialFactory.Create (Env (("AZURE_CLIENT_ID", "11111111-2222-3333-4444-555555555555"))));

  [Theory]
  [InlineData ("")]
  [InlineData ("   ")]
  public void IsHostedInAzure_TreatsBlankValuesAsUnset (string value)
    => Assert.False (AzureCredentialFactory.IsHostedInAzure (Env (("IDENTITY_ENDPOINT", value), ("MSI_ENDPOINT", value))));
}
