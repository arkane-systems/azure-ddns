using AzureDdns.FunctionApp.Config;
using AzureDdns.FunctionApp.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddOptions<RuntimeSettings>().Configure(options =>
        {
            options.DnsSubscriptionId = Environment.GetEnvironmentVariable("DNS_SUBSCRIPTION_ID") ?? string.Empty;
            options.DnsResourceGroup = Environment.GetEnvironmentVariable("DNS_RESOURCE_GROUP") ?? string.Empty;
            options.ConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "config/dyndns.json";
        });

        services.AddSingleton<IConfigProvider, FileConfigProvider>();
        services.AddSingleton<IAuthService, AuthService>();
        services.AddSingleton<IIpResolver, IpResolver>();
        services.AddSingleton<IDnsUpdateService, DnsUpdateService>();
    })
    .Build();

await host.RunAsync();
