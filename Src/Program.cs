using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.WriteIndented = true;
});

builder.Services.AddHttpClient("openrouter", client =>
{
    client.Timeout = TimeSpan.FromMinutes(5);
});

var app = builder.Build();

var logger = app.Logger;
var configuration = app.Configuration;

var listenUrl = configuration["Proxy:ListenUrl"] ?? "http://127.0.0.1:3000";
var openRouterBaseUrl = configuration["Proxy:OpenRouterBaseUrl"] ?? "https://openrouter.ai/api";
var forcedModel = configuration["Proxy:ForcedModel"] ?? "openrouter/free";

var openRouterApiKey =
    Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ??
    configuration["Proxy:OpenRouterApiKey"];

if (string.IsNullOrWhiteSpace(openRouterApiKey))
{
    throw new InvalidOperationException("OPENROUTER_API_KEY is not set.");
}

app.Urls.Clear();
app.Urls.Add(listenUrl);

app.MapGet("/", () => Results.Ok(new
{
    name = "anthropic-proxy-csharp",
    status = "ok",
    forced_model = forcedModel
}));

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok"
}));

app.MapGet("/v1/models", () =>
{
    // Return Anthropic-style model list while internally remapping all traffic.
    return Results.Ok(new
    {
        data = new object[]
        {
            new
            {
                type = "model",
                id = "claude-3-5-haiku-latest",
                display_name = "Claude Haiku (mapped to OpenRouter Free)",
                created_at = "2026-01-01T00:00:00Z"
            },
            new
            {
                type = "model",
                id = "claude-sonnet-4-5",
                display_name = "Claude Sonnet (mapped to OpenRouter Free)",
                created_at = "2026-01-01T00:00:00Z"
            },
            new
            {
                type = "model",
                id = "claude-opus-4-1",
                display_name = "Claude Opus (mapped to OpenRouter Free)",
                created_at = "2026-01-01T00:00:00Z"
            },
            new
            {
                type = "model",
                id = forcedModel,
                display_name = "OpenRouter Free",
                created_at = "2026-01-01T00:00:00Z"
            }
        },
        first_id = "claude-3-5-haiku-latest",
        has_more = false,
        last_id = forcedModel
    });
});

app.MapPost("/v1/messages", async (IHttpClientFactory httpClientFactory, HttpContext httpContext) =>
{
    using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8);
    var requestBody = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(requestBody))
    {
        return Results.BadRequest(new
        {
            error = new
            {
                type = "invalid_request_error",
                message = "Request body is empty."
            }
        });
    }

    JsonNode? jsonBody;
    try
    {
        jsonBody = JsonNode.Parse(requestBody);
    }
    catch (JsonException ex)
    {
        logger.LogWarning(ex, "Failed to parse incoming JSON.");
        return Results.BadRequest(new
        {
            error = new
            {
                type = "invalid_request_error",
                message = ex.Message
            }
        });
    }

    if (jsonBody is not JsonObject bodyObject)
    {
        return Results.BadRequest(new
        {
            error = new
            {
                type = "invalid_request_error",
                message = "JSON body must be an object."
            }
        });
    }

    var incomingModel = bodyObject["model"]?.GetValue<string>() ?? "<missing>";
    bodyObject["model"] = forcedModel;

    if (bodyObject["max_tokens"] is null)
    {
        bodyObject["max_tokens"] = 1024;
    }

    var isStream = bodyObject["stream"]?.GetValue<bool>() ?? false;
    if (isStream)
    {
        logger.LogInformation("Incoming stream=true request for model {IncomingModel}; forcing to {ForcedModel}.", incomingModel, forcedModel);
        return Results.StatusCode(StatusCodes.Status501NotImplemented);
    }

    logger.LogInformation("Proxying /v1/messages model {IncomingModel} -> {ForcedModel}", incomingModel, forcedModel);

    var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, $"{openRouterBaseUrl.TrimEnd('/')}/v1/messages")
    {
        Content = new StringContent(bodyObject.ToJsonString(), Encoding.UTF8, "application/json")
    };

    upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openRouterApiKey);

    if (httpContext.Request.Headers.TryGetValue("anthropic-version", out var anthropicVersion))
    {
        upstreamRequest.Headers.TryAddWithoutValidation("anthropic-version", anthropicVersion.ToString());
    }
    else
    {
        upstreamRequest.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
    }

    upstreamRequest.Headers.TryAddWithoutValidation("HTTP-Referer", "http://127.0.0.1");
    upstreamRequest.Headers.TryAddWithoutValidation("X-Title", "AnthropicProxy");

    var client = httpClientFactory.CreateClient("openrouter");
    HttpResponseMessage upstreamResponse;
    try
    {
        upstreamResponse = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, httpContext.RequestAborted);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to reach OpenRouter.");
        return Results.StatusCode(StatusCodes.Status502BadGateway);
    }

    await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(httpContext.RequestAborted);
    using var upstreamReader = new StreamReader(upstreamStream, Encoding.UTF8);
    var upstreamBody = await upstreamReader.ReadToEndAsync(httpContext.RequestAborted);

    logger.LogInformation("OpenRouter responded with status {StatusCode}", (int)upstreamResponse.StatusCode);

    return Results.Content(
        upstreamBody,
        upstreamResponse.Content.Headers.ContentType?.MediaType ?? "application/json",
        Encoding.UTF8,
        (int)upstreamResponse.StatusCode);
});

app.Run();
