using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.AI.Inference;
using Azure.AI.OpenAI;
using Azure.AI.Projects;
using Azure.Core;
using Azure.Core.Pipeline;
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
        // When checking the APIM gateway (subscription required), pass the subscription key.
        if (!string.IsNullOrWhiteSpace(request.SubscriptionKey))
            req.Headers.TryAddWithoutValidation("api-key", request.SubscriptionKey);
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
        // When the endpoint is the APIM gateway (subscription required), forward the
        // subscription key as the api-key header on every call.
        if (!string.IsNullOrWhiteSpace(request.SubscriptionKey))
        {
            options.AddPolicy(
                new SubscriptionKeyPolicy(request.SubscriptionKey),
                System.ClientModel.Primitives.PipelinePosition.PerCall);
        }
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

app.MapPost("/api/check-foundry", async (FoundryCheckRequest request, IHttpClientFactory httpFactory) =>
{
    if (string.IsNullOrWhiteSpace(request.Endpoint))
        return Results.BadRequest(new { error = "Endpoint is required." });
    if (!Uri.TryCreate(NormalizeFoundryEndpoint(request.Endpoint), UriKind.Absolute, out var endpointUri))
        return Results.BadRequest(new { error = "Endpoint must be a valid absolute URL." });

    try
    {
        var credential = new DefaultAzureCredential();
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }));

        var url = $"{endpointUri.ToString().TrimEnd('/')}/info?api-version=2024-05-01-preview";
        using var http = httpFactory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
        if (!string.IsNullOrWhiteSpace(request.SubscriptionKey))
            req.Headers.TryAddWithoutValidation("api-key", request.SubscriptionKey);
        using var resp = await http.SendAsync(req);

        var status = (int)resp.StatusCode;
        var ready = resp.IsSuccessStatusCode;
        var message = ready
            ? "Reachable and authorized — token (Cognitive Services audience) accepted by the model-inference endpoint."
            : status is 401 or 403
                ? "Access denied — the identity lacks a role on the Foundry resource, or it hasn't propagated yet (wait ~15-20 min)."
                : status is 404
                    ? "Endpoint reached but /info not found — check the endpoint URL (should be the services.ai.azure.com host)."
                    : $"Unexpected status {status}.";
        return Results.Ok(new { status, ready, message });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { status = 0, ready = false, message = ex.Message });
    }
});

// Foundry chat completion via Azure.AI.Inference ChatCompletionsClient.
app.MapPost("/api/chat-foundry", async (FoundryChatRequest request, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Endpoint))
        return Results.BadRequest(new { error = "Endpoint is required." });
    if (string.IsNullOrWhiteSpace(request.Model))
        return Results.BadRequest(new { error = "Model is required." });
    if (request.Messages is null || request.Messages.Count == 0)
        return Results.BadRequest(new { error = "At least one message is required." });
    if (!Uri.TryCreate(NormalizeFoundryEndpoint(request.Endpoint), UriKind.Absolute, out var endpointUri))
        return Results.BadRequest(new { error = "Endpoint must be a valid absolute URL." });

    try
    {
        // Disable retries so upstream 429s (e.g. APIM rate-limit) surface immediately.
        var options = new AzureAIInferenceClientOptions
        {
            Retry = { MaxRetries = 0 },
        };
        // When the endpoint is the APIM gateway, forward the subscription key.
        if (!string.IsNullOrWhiteSpace(request.SubscriptionKey))
            options.AddPolicy(new InferenceApiKeyPolicy(request.SubscriptionKey), HttpPipelinePosition.PerCall);

        // Force the Cognitive Services token audience (SDK default is ml.azure.com).
        var credential = new FoundryScopeCredential(new DefaultAzureCredential());
        var client = new ChatCompletionsClient(endpointUri, credential, options);

        var chatOptions = new ChatCompletionsOptions { Model = request.Model };
        foreach (var m in request.Messages)
        {
            chatOptions.Messages.Add(m.Role?.ToLowerInvariant() switch
            {
                "system" => new ChatRequestSystemMessage(m.Content),
                "assistant" => new ChatRequestAssistantMessage(m.Content),
                _ => new ChatRequestUserMessage(m.Content),
            });
        }

        logger.LogInformation(
            "Calling Foundry model inference. requestPath={RequestPath} endpointUrl={EndpointUrl} model={Model}",
            "/api/chat-foundry", endpointUri.ToString().TrimEnd('/'), request.Model);

        Response<ChatCompletions> response = await client.CompleteAsync(chatOptions);
        var reply = response.Value.Content ?? "";

        logger.LogInformation("Successful /api/chat-foundry call. requestPath={RequestPath}", "/api/chat-foundry");

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

// ---------------------------------------------------------------------------
// Project client endpoints (Azure.AI.Projects AIProjectClient + Responses API).
//
// "Check" uses AIProjectClient to confirm the project is reachable. "Chat" calls
// the project's OpenAI-compatible *Responses API* ({project}/openai/v1/responses)
// — the surface AIProjectClient.GetOpenAIClient() targets. Set the model to a
// plain deployment name for a direct call, or to a BYOM "<connection>/<model>"
// name to route the response through the connected APIM gateway (no agent needed).
// ---------------------------------------------------------------------------

app.MapPost("/api/check-project", (ProjectCheckRequest request, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Endpoint))
        return Results.BadRequest(new { error = "Project endpoint is required." });
    if (!Uri.TryCreate(request.Endpoint.Trim().TrimEnd('/'), UriKind.Absolute, out var projUri))
        return Results.BadRequest(new { error = "Project endpoint must be a valid absolute URL." });

    try
    {
        var projectClient = new AIProjectClient(projUri, new DefaultAzureCredential());
        var connection = projectClient.GetConnection(typeof(AzureOpenAIClient).FullName!);
        var host = connection.TryGetLocatorAsUri(out var uri) && uri is not null ? $"https://{uri.Host}" : "(none)";
        return Results.Ok(new { status = 200, ready = true, message = $"AIProjectClient reached the project (default Azure OpenAI connection: {host}). Chat uses the project's Responses API." });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { status = 0, ready = false, message = ex.Message });
    }
});

app.MapPost("/api/chat-project", async (ProjectChatRequest request, IHttpClientFactory httpFactory, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Endpoint))
        return Results.BadRequest(new { error = "Project endpoint is required." });
    if (string.IsNullOrWhiteSpace(request.Deployment))
        return Results.BadRequest(new { error = "Model (plain deployment or \"<connection>/<model>\") is required." });
    if (request.Messages is null || request.Messages.Count == 0)
        return Results.BadRequest(new { error = "At least one message is required." });
    if (!Uri.TryCreate(request.Endpoint.Trim().TrimEnd('/'), UriKind.Absolute, out _))
        return Results.BadRequest(new { error = "Project endpoint must be a valid absolute URL." });

    try
    {
        var baseUrl = request.Endpoint.Trim().TrimEnd('/');
        var url = $"{baseUrl}/openai/v1/responses";

        // Pass the full turn history as the Responses API "input" so context is
        // preserved without managing a server-side conversation.
        var input = request.Messages.Select(m => new
        {
            role = string.IsNullOrWhiteSpace(m.Role) ? "user" : m.Role.ToLowerInvariant(),
            content = m.Content,
        }).ToList();
        var body = new { model = request.Deployment, input };

        using var http = httpFactory.CreateClient();
        using var req = await BuildFoundryRequestAsync(HttpMethod.Post, url, body);
        using var resp = await http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            return Results.Problem(detail: json, statusCode: (int)resp.StatusCode);

        var reply = ExtractOutputText(json);
        logger.LogInformation("Project-client chat via Responses API. model={Model}", request.Deployment);
        return Results.Ok(new { reply });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});

// ---------------------------------------------------------------------------
// Foundry prompt-agent (BYOM) endpoints.
//
// These exercise the *project endpoint* (https://<acct>.services.ai.azure.com/
// api/projects/<project>) via the new prompt-agent + OpenAI Responses surface.
// A BYOM model deployment ("<connection-name>/<model-name>") routes the agent's
// inference through the connected APIM gateway on the backend — so a successful
// reply here, plus a hit in APIM analytics, proves the project endpoint is using
// the gateway. BYOM models are agent-only; they can't be called via plain
// chat/completions, which is why this needs its own tab.
//
// Auth: the project endpoint expects an AAD token with the https://ai.azure.com
// audience (NOT cognitiveservices), and the caller needs the "Foundry User" role
// on the project. No APIM subscription key is needed here — that credential lives
// inside the Foundry connection, between Foundry and APIM.
// ---------------------------------------------------------------------------

// Create (or update) a prompt-agent version that references the BYOM model.
app.MapPost("/api/foundry-agent/create", async (FoundryAgentCreateRequest request, IHttpClientFactory httpFactory, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Endpoint))
        return Results.BadRequest(new { error = "Project endpoint is required." });
    if (string.IsNullOrWhiteSpace(request.AgentName))
        return Results.BadRequest(new { error = "Agent name is required." });
    if (string.IsNullOrWhiteSpace(request.Model))
        return Results.BadRequest(new { error = "Model (\"<connection-name>/<model-name>\") is required." });
    if (!Uri.TryCreate(request.Endpoint.Trim().TrimEnd('/'), UriKind.Absolute, out _))
        return Results.BadRequest(new { error = "Project endpoint must be a valid absolute URL." });

    try
    {
        var baseUrl = request.Endpoint.Trim().TrimEnd('/');
        var url = $"{baseUrl}/agents/{Uri.EscapeDataString(request.AgentName)}/versions?api-version=v1";
        var body = new
        {
            definition = new
            {
                kind = "prompt",
                model = request.Model,
                instructions = string.IsNullOrWhiteSpace(request.Instructions) ? null : request.Instructions,
            },
        };

        using var http = httpFactory.CreateClient();
        using var req = await BuildFoundryRequestAsync(HttpMethod.Post, url, body);
        using var resp = await http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            return Results.Problem(detail: json, statusCode: (int)resp.StatusCode);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : request.AgentName;
        var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
        var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;

        logger.LogInformation("Created prompt agent. name={Name} version={Version} status={Status}", name, version, status);
        return Results.Ok(new { name, version, status });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});

// Send a message to the agent. Creates the conversation on first turn (when
// conversationId is empty) and reuses it afterward so context is preserved.
app.MapPost("/api/foundry-agent/chat", async (FoundryAgentChatRequest request, IHttpClientFactory httpFactory, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Endpoint))
        return Results.BadRequest(new { error = "Project endpoint is required." });
    if (string.IsNullOrWhiteSpace(request.AgentName))
        return Results.BadRequest(new { error = "Agent name is required." });
    if (string.IsNullOrWhiteSpace(request.Message))
        return Results.BadRequest(new { error = "Message is required." });
    if (!Uri.TryCreate(request.Endpoint.Trim().TrimEnd('/'), UriKind.Absolute, out _))
        return Results.BadRequest(new { error = "Project endpoint must be a valid absolute URL." });

    try
    {
        var baseUrl = request.Endpoint.Trim().TrimEnd('/');
        var openAiBase = $"{baseUrl}/openai/v1";
        var userItem = new { type = "message", role = "user", content = request.Message };

        using var http = httpFactory.CreateClient();
        var conversationId = request.ConversationId;

        if (string.IsNullOrWhiteSpace(conversationId))
        {
            using var convReq = await BuildFoundryRequestAsync(HttpMethod.Post, $"{openAiBase}/conversations", new { items = new[] { userItem } });
            using var convResp = await http.SendAsync(convReq);
            var convJson = await convResp.Content.ReadAsStringAsync();
            if (!convResp.IsSuccessStatusCode)
                return Results.Problem(detail: convJson, statusCode: (int)convResp.StatusCode);
            using var convDoc = JsonDocument.Parse(convJson);
            conversationId = convDoc.RootElement.GetProperty("id").GetString();
        }
        else
        {
            using var itemReq = await BuildFoundryRequestAsync(HttpMethod.Post, $"{openAiBase}/conversations/{Uri.EscapeDataString(conversationId)}/items", new { items = new[] { userItem } });
            using var itemResp = await http.SendAsync(itemReq);
            if (!itemResp.IsSuccessStatusCode)
            {
                var itemJson = await itemResp.Content.ReadAsStringAsync();
                return Results.Problem(detail: itemJson, statusCode: (int)itemResp.StatusCode);
            }
        }

        var responseBody = new
        {
            conversation = conversationId,
            agent_reference = new { name = request.AgentName, type = "agent_reference" },
        };
        using var respReq = await BuildFoundryRequestAsync(HttpMethod.Post, $"{openAiBase}/responses", responseBody);
        using var respResp = await http.SendAsync(respReq);
        var respJson = await respResp.Content.ReadAsStringAsync();
        if (!respResp.IsSuccessStatusCode)
            return Results.Problem(detail: respJson, statusCode: (int)respResp.StatusCode);

        var reply = ExtractOutputText(respJson);
        logger.LogInformation("Agent response received. agent={Agent} conversationId={ConversationId}", request.AgentName, conversationId);
        return Results.Ok(new { reply, conversationId });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});

// Delete the conversation (if any) and the agent version, cleaning up the agent.
app.MapPost("/api/foundry-agent/delete", async (FoundryAgentDeleteRequest request, IHttpClientFactory httpFactory, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(request.Endpoint))
        return Results.BadRequest(new { error = "Project endpoint is required." });
    if (string.IsNullOrWhiteSpace(request.AgentName))
        return Results.BadRequest(new { error = "Agent name is required." });
    if (!Uri.TryCreate(request.Endpoint.Trim().TrimEnd('/'), UriKind.Absolute, out _))
        return Results.BadRequest(new { error = "Project endpoint must be a valid absolute URL." });

    try
    {
        var baseUrl = request.Endpoint.Trim().TrimEnd('/');
        using var http = httpFactory.CreateClient();
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.ConversationId))
        {
            using var convReq = await BuildFoundryRequestAsync(HttpMethod.Delete, $"{baseUrl}/openai/v1/conversations/{Uri.EscapeDataString(request.ConversationId)}", null);
            using var convResp = await http.SendAsync(convReq);
            if (!convResp.IsSuccessStatusCode && convResp.StatusCode != System.Net.HttpStatusCode.NotFound)
                errors.Add($"conversation delete: {(int)convResp.StatusCode} {await convResp.Content.ReadAsStringAsync()}");
        }

        if (!string.IsNullOrWhiteSpace(request.Version))
        {
            using var verReq = await BuildFoundryRequestAsync(HttpMethod.Delete, $"{baseUrl}/agents/{Uri.EscapeDataString(request.AgentName)}/versions/{Uri.EscapeDataString(request.Version)}?api-version=v1&force=true", null);
            using var verResp = await http.SendAsync(verReq);
            if (!verResp.IsSuccessStatusCode && verResp.StatusCode != System.Net.HttpStatusCode.NotFound)
                errors.Add($"agent version delete: {(int)verResp.StatusCode} {await verResp.Content.ReadAsStringAsync()}");
        }

        if (errors.Count > 0)
            return Results.Problem(detail: string.Join("; ", errors), statusCode: 502);

        logger.LogInformation("Deleted agent {Agent} (version {Version}).", request.AgentName, request.Version);
        return Results.Ok(new { deleted = true });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});

app.Run();

// Builds an authenticated request to the Foundry project endpoint. The project
// endpoint requires an AAD token with the https://ai.azure.com audience.
static async Task<HttpRequestMessage> BuildFoundryRequestAsync(HttpMethod method, string url, object? body)
{
    var credential = new DefaultAzureCredential();
    var token = await credential.GetTokenAsync(
        new TokenRequestContext(new[] { "https://ai.azure.com/.default" }));

    var req = new HttpRequestMessage(method, url);
    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);
    if (body is not null)
        req.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
    return req;
}

// Extracts the assistant reply from an OpenAI Responses payload by concatenating
// every output_text segment under output[].content[].
static string ExtractOutputText(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.String)
            return ot.GetString() ?? "";

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var t) && t.GetString() == "output_text"
                        && part.TryGetProperty("text", out var text))
                        sb.Append(text.GetString());
                }
            }
            if (sb.Length > 0)
                return sb.ToString();
        }
        return json;
    }
    catch
    {
        return json;
    }
}

// Normalizes a Foundry endpoint: appends "/models" unless the path already ends
// in that segment, since Azure.AI.Inference posts to {endpoint}/chat/completions.
// Works for both the direct host (https://x.services.ai.azure.com/) and an APIM
// gateway path (https://x.azure-api.net/azureai), which both expose /models/chat/completions.
static string NormalizeFoundryEndpoint(string endpoint)
{
    var trimmed = endpoint.Trim().TrimEnd('/');
    if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        return trimmed;
    var lastSegment = uri.AbsolutePath.Trim('/').Split('/').LastOrDefault() ?? "";
    return string.Equals(lastSegment, "models", StringComparison.OrdinalIgnoreCase)
        ? trimmed
        : $"{trimmed}/models";
}

record ChatConfig(string Endpoint, string Deployment);

record CheckRequest(
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("subscriptionKey")] string? SubscriptionKey);

record ChatRequest(
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("deployment")] string Deployment,
    [property: JsonPropertyName("subscriptionKey")] string? SubscriptionKey,
    [property: JsonPropertyName("messages")] List<ChatMessageDto> Messages);

record ChatMessageDto(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

record FoundryCheckRequest(
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("subscriptionKey")] string? SubscriptionKey);

record FoundryChatRequest(
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("subscriptionKey")] string? SubscriptionKey,
    [property: JsonPropertyName("messages")] List<ChatMessageDto> Messages);

record ProjectCheckRequest(
    [property: JsonPropertyName("endpoint")] string Endpoint);

record ProjectChatRequest(
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("deployment")] string Deployment,
    [property: JsonPropertyName("messages")] List<ChatMessageDto> Messages);

record FoundryAgentCreateRequest(
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("agentName")] string AgentName,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("instructions")] string? Instructions);

record FoundryAgentChatRequest(
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("agentName")] string AgentName,
    [property: JsonPropertyName("conversationId")] string? ConversationId,
    [property: JsonPropertyName("message")] string Message);

record FoundryAgentDeleteRequest(
    [property: JsonPropertyName("endpoint")] string Endpoint,
    [property: JsonPropertyName("agentName")] string AgentName,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("conversationId")] string? ConversationId);

// Adds the APIM subscription key header to each outgoing request when calling the gateway.
sealed class SubscriptionKeyPolicy(string subscriptionKey) : System.ClientModel.Primitives.PipelinePolicy
{
    private const string HeaderName = "api-key";

    public override void Process(
        System.ClientModel.Primitives.PipelineMessage message,
        IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline,
        int currentIndex)
    {
        message.Request.Headers.Set(HeaderName, subscriptionKey);
        ProcessNext(message, pipeline, currentIndex);
    }

    public override ValueTask ProcessAsync(
        System.ClientModel.Primitives.PipelineMessage message,
        IReadOnlyList<System.ClientModel.Primitives.PipelinePolicy> pipeline,
        int currentIndex)
    {
        message.Request.Headers.Set(HeaderName, subscriptionKey);
        return ProcessNextAsync(message, pipeline, currentIndex);
    }
}

// Azure.Core pipeline policy (used by Azure.AI.Inference) that adds the APIM
// subscription key header on the Foundry path when calling the gateway.
sealed class InferenceApiKeyPolicy(string subscriptionKey) : HttpPipelineSynchronousPolicy
{
    public override void OnSendingRequest(HttpMessage message)
        => message.Request.Headers.SetValue("api-key", subscriptionKey);
}

// Wraps a TokenCredential to always request the Cognitive Services audience,
// overriding the Azure.AI.Inference SDK default scope (https://ml.azure.com/.default),
// which a Foundry AI Services resource rejects.
sealed class FoundryScopeCredential(TokenCredential inner) : TokenCredential
{
    private static readonly string[] Scopes = { "https://cognitiveservices.azure.com/.default" };

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => inner.GetToken(new TokenRequestContext(Scopes, requestContext.ParentRequestId), cancellationToken);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => inner.GetTokenAsync(new TokenRequestContext(Scopes, requestContext.ParentRequestId), cancellationToken);
}
