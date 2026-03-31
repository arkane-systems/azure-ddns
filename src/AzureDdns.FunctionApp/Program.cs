#region header

// AzureDdns.FunctionApp - Program.cs
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
using AzureDdns.FunctionApp.Services;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

#endregion

// Runtime settings are bound from environment variables to keep deployment configuration external.
// This project intentionally keeps configuration simple and file-based (CONFIG_PATH) for maintainability.
IHost host = new HostBuilder ()
            .ConfigureFunctionsWebApplication ()
            .ConfigureServices (static services =>
                                {
                                  services.AddOptions<RuntimeSettings> ()
                                          .Configure (static options =>
                                                      {
                                                        // Subscription/resource group define the Azure DNS management scope.
                                                        options.DnsSubscriptionId =
                                                          Environment.GetEnvironmentVariable ("DNS_SUBSCRIPTION_ID") ??
                                                          string.Empty;
                                                        options.DnsResourceGroup =
                                                          Environment.GetEnvironmentVariable ("DNS_RESOURCE_GROUP") ??
                                                          string.Empty;

                                                        // Config path defaults to packaged app content for predictable deployments.
                                                        options.ConfigPath =
                                                          Environment.GetEnvironmentVariable ("CONFIG_PATH") ??
                                                          "config/dyndns.json";
                                                      });

                                  // Service registrations remain singleton because services are stateless or config-backed.
                                  services.AddSingleton<IConfigProvider, FileConfigProvider> ();
                                  services.AddSingleton<IAuthService, AuthService> ();
                                  services.AddSingleton<IIpResolver, IpResolver> ();
                                  services.AddSingleton<IDnsUpdateService, DnsUpdateService> ();
                                })
            .Build ();

await host.RunAsync ();
