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

// Debug check: performs a read-only call (GET /openai/models) against the endpoint.
// This confirms the endpoint is reachable AND the identity's role assignment has landed.
// Read access propagates *sooner* than the chat/completions action, so a 200 here while
// chat still 401s tells you the role is assigned and you're just waiting on propagation —
// not that something else (wrong endpoint, missing role) is misconfigured.
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
            ? "Read access OK — role assignment has landed. If chat still 401s, it's just propagation (wait ~15-20 min); read propagates before the chat action."
            : status is 401 or 403
                ? "Access denied — role assignment is missing or hasn't started propagating yet."
                : status is 404
                    ? "Endpoint not found — check the endpoint URL."
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
app.MapPost("/api/chat", async (ChatRequest request, ILogger<Program> logger) =>
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
        // Disable automatic retries so upstream 429s (e.g. APIM rate-limit) surface
        // immediately instead of being retried/masked by the SDK.
        var options = new AzureOpenAIClientOptions
        {
            RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 0),
        };
        var client = new AzureOpenAIClient(endpointUri, new DefaultAzureCredential(), options);
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

        var endpointBaseUrl = request.Endpoint.TrimEnd('/');
        const string apiVersion = "2024-10-21";
        var upstreamChatUrl =
            $"{endpointBaseUrl}/openai/deployments/{Uri.EscapeDataString(request.Deployment)}/chat/completions?api-version={apiVersion}";

        logger.LogInformation(
            "Calling upstream chat completion. requestPath={RequestPath} endpointUrl={EndpointUrl} upstreamUrl={UpstreamUrl} deployment={Deployment}",
            "/api/chat",
            endpointBaseUrl,
            upstreamChatUrl,
            request.Deployment);

        ChatCompletion completion = await chatClient.CompleteChatAsync(messages);
        var reply = completion.Content.Count > 0 ? completion.Content[0].Text : "";

        logger.LogInformation("Successful /api/chat call. requestPath={RequestPath}", "/api/chat");

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
