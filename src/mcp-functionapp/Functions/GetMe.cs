using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace FunctionApp.Functions;

public class GetMe
{
    private readonly ILogger<GetMe> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public GetMe(ILogger<GetMe> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    [Function("GetMe")]
    public async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "me")] HttpRequest req)
    {
        // The Authorization header contains the OBO Graph token set by APIM
        var authHeader = req.Headers.Authorization.FirstOrDefault();

        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("GetMe called without a valid Bearer token");
            return new UnauthorizedObjectResult(new
            {
                error = "missing_token",
                error_description = "A Bearer token is required in the Authorization header."
            });
        }

        var token = authHeader["Bearer ".Length..];

        try
        {
            var client = _httpClientFactory.CreateClient("GraphApi");
            using var request = new HttpRequestMessage(HttpMethod.Get, "me");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogError("Graph API returned {StatusCode}: {Body}", response.StatusCode, errorBody);
                return new ObjectResult(new
                {
                    error = "graph_api_error",
                    status_code = (int)response.StatusCode,
                    details = errorBody
                })
                {
                    StatusCode = (int)response.StatusCode
                };
            }

            var graphResponse = await response.Content.ReadAsStringAsync();
            _logger.LogInformation("Successfully retrieved user profile from Graph API");

            return new ContentResult
            {
                Content = graphResponse,
                ContentType = "application/json",
                StatusCode = 200
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Graph API");
            return new ObjectResult(new
            {
                error = "internal_error",
                error_description = "An error occurred while calling Microsoft Graph."
            })
            {
                StatusCode = 500
            };
        }
    }
}
