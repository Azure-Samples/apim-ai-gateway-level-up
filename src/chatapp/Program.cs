using System.Text.Json.Serialization;
using Azure;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using OpenAI.Chat;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
var app = builder.Build();

// Serve the static chat page from wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

// Default endpoint/deployment used to pre-fill the page (override in appsettings.json).
var defaultEndpoint = app.Configuration["Chat:Endpoint"] ?? "";
var defaultDeployment = app.Configuration["Chat:Deployment"] ?? "gpt-4.1-mini";

app.MapGet("/api/config", () => Results.Ok(new ChatConfig(defaultEndpoint, defaultDeployment)));

// Debug check: calls the read-only /openai/models endpoint to confirm the endpoint is
// reachable and the current identity has data-plane access. A 200 means you're ready to
// chat; a 401/403 usually means the role assignment is still propagating (can take ~15-20 min).
app.MapPost("/api/check", async (CheckRequest request, IHttpClientFactory httpFactory) =>
{
    if (string.IsNullOrWhiteSpace(request.Endpoint))
        return Results.BadRequest(new { error = "Endpoint is required." });
    if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var endpointUri))
        return Results.BadRequest(new { error = "Endpoint must be a valid absolute URL." });

    try
    {
        var credential = new DefaultAzureCredential();
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }));

        var url = $"{request.Endpoint.TrimEnd('/')}/openai/models?api-version=2024-10-21";
        using var http = httpFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        using var resp = await http.SendAsync(req);

        var status = (int)resp.StatusCode;
        var ready = resp.IsSuccessStatusCode;
        var message = ready
            ? "Ready — endpoint reachable and identity has data-plane access."
            : status is 401 or 403
                ? "Access denied — role assignment may still be propagating (wait ~15-20 min) or is missing."
                : $"Unexpected status {status}.";
        return Results.Ok(new { status, ready, message });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { status = 0, ready = false, message = ex.Message });
    }
});

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

record CheckRequest(
    [property: JsonPropertyName("endpoint")] string Endpoint);

record ChatRequest(
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("deployment")] string Deployment,
    [property: JsonPropertyName("messages")] List<ChatMessageDto> Messages);

record ChatMessageDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);
