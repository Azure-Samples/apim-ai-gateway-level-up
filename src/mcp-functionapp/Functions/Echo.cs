using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FunctionApp.Functions;

public class Echo
{
    private readonly ILogger<Echo> _logger;

    public Echo(ILogger<Echo> logger)
    {
        _logger = logger;
    }

    [Function("Echo")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "echo")] HttpRequest req)
    {
        var name = req.Query["name"].FirstOrDefault() ?? "World";
        _logger.LogInformation("Echo function called with name: {Name}", name);

        return new OkObjectResult(new { message = $"Echo: {name}" });
    }
}
