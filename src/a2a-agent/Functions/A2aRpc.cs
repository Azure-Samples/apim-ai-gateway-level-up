using System.Text.Json;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace A2aAgent.Functions;

// Minimal A2A JSON-RPC 2.0 endpoint. Implements the `message/send` method: it reads the user's
// text, asks a Foundry model to summarize it, and returns an A2A Message with the result.
public class A2aRpc
{
    private readonly ILogger<A2aRpc> _logger;
    private readonly AzureOpenAIClient? _openAiClient;

    public A2aRpc(ILogger<A2aRpc> logger, AzureOpenAIClient? openAiClient = null)
    {
        _logger = logger;
        _openAiClient = openAiClient;
    }

    [Function("A2aRpc")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "a2a")] HttpRequest req)
    {
        JsonElement root;
        try
        {
            using var doc = await JsonDocument.ParseAsync(req.Body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonRpcError(null, -32700, "Parse error");
        }

        var id = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : (JsonElement?)null;
        var method = root.TryGetProperty("method", out var methodEl) ? methodEl.GetString() : null;

        if (method != "message/send")
        {
            return JsonRpcError(id, -32601, $"Method not found: {method}");
        }

        var userText = ExtractText(root);
        if (string.IsNullOrWhiteSpace(userText))
        {
            return JsonRpcError(id, -32602, "Invalid params: no text part found in message.");
        }

        string summary;
        try
        {
            summary = await SummarizeAsync(userText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model call failed");
            return JsonRpcError(id, -32000, $"Agent error: {ex.Message}");
        }

        // A2A result: a Message from the agent (kind = "message").
        var result = new
        {
            kind = "message",
            role = "agent",
            messageId = Guid.NewGuid().ToString(),
            parts = new[]
            {
                new { kind = "text", text = summary }
            }
        };

        return JsonRpcResult(id, result);
    }

    private async Task<string> SummarizeAsync(string userText)
    {
        var deployment = Environment.GetEnvironmentVariable("MODEL_DEPLOYMENT") ?? "gpt-4.1-mini";

        if (_openAiClient is null)
        {
            // FOUNDRY_ENDPOINT not configured — degrade gracefully so the A2A wiring is still testable.
            _logger.LogWarning("FOUNDRY_ENDPOINT not set; returning a stub summary.");
            var trimmed = userText.Length > 200 ? userText[..200] + "…" : userText;
            return $"[stub summary — configure FOUNDRY_ENDPOINT to use the model] {trimmed}";
        }

        ChatClient chat = _openAiClient.GetChatClient(deployment);
        var messages = new ChatMessage[]
        {
            new SystemChatMessage("You are a summarization agent. Reply with a concise summary of the user's text and nothing else."),
            new UserChatMessage(userText)
        };

        var completion = await chat.CompleteChatAsync(messages);
        return completion.Value.Content.Count > 0 ? completion.Value.Content[0].Text : "";
    }

    // Pulls the concatenated text of all text parts from params.message.parts.
    private static string ExtractText(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p) ||
            !p.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        var texts = new List<string>();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                var t = textEl.GetString();
                if (!string.IsNullOrWhiteSpace(t))
                {
                    texts.Add(t);
                }
            }
        }

        return string.Join("\n", texts);
    }

    private static ContentResult JsonRpcResult(JsonElement? id, object result)
        => Envelope(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.HasValue ? (object?)JsonSerializer.Deserialize<object>(id.Value.GetRawText()) : null,
            ["result"] = result
        });

    private static ContentResult JsonRpcError(JsonElement? id, int code, string message)
        => Envelope(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.HasValue ? (object?)JsonSerializer.Deserialize<object>(id.Value.GetRawText()) : null,
            ["error"] = new { code, message }
        });

    private static ContentResult Envelope(object payload)
        => new()
        {
            Content = JsonSerializer.Serialize(payload),
            ContentType = "application/json",
            StatusCode = 200
        };
}
