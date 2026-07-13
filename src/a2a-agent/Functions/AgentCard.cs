using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace A2aAgent.Functions;

// Serves the A2A Agent Card (the agent's "business card") at the well-known discovery path.
// See https://a2a-protocol.org/dev/specification/#5-agent-discovery-the-agent-card
public class AgentCard
{
    private readonly ILogger<AgentCard> _logger;

    public AgentCard(ILogger<AgentCard> logger)
    {
        _logger = logger;
    }

    [Function("AgentCard")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = ".well-known/agent-card.json")] HttpRequest req)
    {
        var baseUrl = $"{req.Scheme}://{req.Host}";
        _logger.LogInformation("Serving agent card for base URL {BaseUrl}", baseUrl);

        var card = new
        {
            protocolVersion = "0.2.9",
            name = "Summarizer Agent",
            description = "An A2A agent that summarizes text using an Azure AI Foundry model, governed by Azure API Management.",
            url = $"{baseUrl}/a2a",
            preferredTransport = "JSONRPC",
            version = "1.0.0",
            capabilities = new
            {
                streaming = false,
                pushNotifications = false,
                stateTransitionHistory = false
            },
            defaultInputModes = new[] { "text/plain" },
            defaultOutputModes = new[] { "text/plain" },
            skills = new[]
            {
                new
                {
                    id = "summarize",
                    name = "Summarize text",
                    description = "Produces a concise summary of the text supplied in the message.",
                    tags = new[] { "text", "summarization" },
                    examples = new[]
                    {
                        "Summarize: <paste a few paragraphs here>",
                        "Give me a one-sentence summary of this article."
                    }
                }
            }
        };

        return new ContentResult
        {
            Content = JsonSerializer.Serialize(card, new JsonSerializerOptions { WriteIndented = true }),
            ContentType = "application/json",
            StatusCode = 200
        };
    }
}
