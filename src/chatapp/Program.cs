using System.Text.Json.Serialization;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Chat;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Serve the static chat page from wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

// Default endpoint/deployment used to pre-fill the page (override in appsettings.json).
var defaultEndpoint = app.Configuration["Chat:Endpoint"] ?? "";
var defaultDeployment = app.Configuration["Chat:Deployment"] ?? "gpt-4.1-mini";

app.MapGet("/api/config", () => Results.Ok(new ChatConfig(defaultEndpoint, defaultDeployment)));

// Chat completion. The endpoint is supplied per request so it can be swapped
// (e.g. from the Foundry URL to the APIM gateway URL) without restarting the app.
app.MapPost("/api/chat", async (ChatRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Endpoint))
        return Results.BadRequest(new { error = "Endpoint is required." });
    if (string.IsNullOrWhiteSpace(request.Deployment))
        return Results.BadRequest(new { error = "Deployment is required." });
    if (request.Messages is null || request.Messages.Count == 0)
        return Results.BadRequest(new { error = "At least one message is required." });

    if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpointUri))
        return Results.BadRequest(new { error = "Endpoint must be a valid absolute URL." });

    try
    {
        // DefaultAzureCredential: uses your local `az login` identity (or a managed
        // identity when hosted). The identity needs the "Cognitive Services OpenAI User"
        // role on the Foundry account.
        var client = new AzureOpenAIClient(endpointUri, new DefaultAzureCredential());
        ChatClient chatClient = client.GetChatClient(request.Deployment);

        var messages = new List<ChatMessage>();
        foreach (var m in request.Messages)
        {
            messages.Add(m.Role?.ToLowerInvariant() switch
            {
                "system" => new SystemChatMessage(m.Content),
                "assistant" => new AssistantChatMessage(m.Content),
                _ => new UserChatMessage(m.Content),
            });
        }

        ChatCompletion completion = await chatClient.CompleteChatAsync(messages);
        var reply = completion.Content.Count > 0 ? completion.Content[0].Text : "";
        return Results.Ok(new { reply });
    }
    catch (RequestFailedException ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: ex.Status == 0 ? 502 : ex.Status);
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});

app.Run();

record ChatConfig(string Endpoint, string Deployment);

record ChatRequest(
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("deployment")] string Deployment,
    [property: JsonPropertyName("messages")] List<ChatMessageDto> Messages);

record ChatMessageDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);
