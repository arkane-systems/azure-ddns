using AzureDdns.FunctionApp.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AzureDdns.FunctionApp.Functions;

public sealed class UpdateDnsFunction
{
    private readonly ILogger<UpdateDnsFunction> _logger;

    public UpdateDnsFunction(ILogger<UpdateDnsFunction> logger)
    {
        _logger = logger;
    }

    [Function("UpdateDns")]
    public IActionResult Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "update")] HttpRequest request)
    {
        var client = request.Query["client"].ToString();
        var zone = request.Query["zone"].ToString();
        var name = request.Query["name"].ToString();

        _logger.LogInformation("Received DDNS scaffold request for client {Client}, zone {Zone}, record {Record}.", client, zone, name);

        return new ContentResult
        {
            Content = "ERROR: endpoint scaffolded; DDNS implementation pending",
            ContentType = "text/plain",
            StatusCode = StatusCodes.Status501NotImplemented,
        };
    }
}
