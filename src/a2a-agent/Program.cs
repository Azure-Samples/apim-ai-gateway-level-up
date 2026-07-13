using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        // A single AzureOpenAIClient reused across invocations. Endpoint + identity come from
        // app settings so the agent can point at Foundry directly (default) or the APIM Foundry
        // gateway. DefaultAzureCredential picks the Function App's user-assigned identity via the
        // AZURE_CLIENT_ID app setting. When FOUNDRY_ENDPOINT is unset, the client isn't registered
        // and the agent falls back to a stub summary so the A2A wiring is still testable.
        var endpoint = Environment.GetEnvironmentVariable("FOUNDRY_ENDPOINT");
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            services.AddSingleton(new AzureOpenAIClient(new Uri(endpoint), new DefaultAzureCredential()));
        }
    })
    .Build();

host.Run();
