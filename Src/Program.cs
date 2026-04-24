using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
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
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    MaxConnectionsPerServer = 10,
    UseCookies = false,
    AutomaticDecompression = System.Net.DecompressionMethods.None,
    SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                 | System.Security.Authentication.SslProtocols.Tls13,
});

var app = builder.Build();
var logger = app.Logger;
var configuration = app.Configuration;

var listenUrl = configuration["Proxy:ListenUrl"] ?? "http://127.0.0.1:3000";
var openRouterBaseUrl = configuration["Proxy:OpenRouterBaseUrl"] ?? "https://openrouter.ai/api";
var forcedModel = configuration["Proxy:ForcedModel"] ?? "qwen/qwen3-coder:free";

// Список фолбэк-моделей из конфига (или дефолтный)
var fallbackModels = configuration
    .GetSection("Proxy:FallbackModels")
    .Get<string[]>()
    ?? [
        "qwen/qwen3-coder:free",
        "meta-llama/llama-3.3-70b-instruct:free",
        "nvidia/nemotron-3-super-120b-a12b:free",
        "openai/gpt-oss-20b:free",
    ];

// API-ключ: сначала из appsettings.json, потом из environment (environment имеет приоритет в ASP.NET Core)
var openRouterApiKey =
    configuration["Proxy:OpenRouterApiKey"] ??
    Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

if (string.IsNullOrWhiteSpace(openRouterApiKey))
    throw new InvalidOperationException(
        "OpenRouter API key is not set. Add it to appsettings.json: Proxy:OpenRouterApiKey");

// Модели, которые возвращают 400 на Anthropic-формат — пропускаем их совсем
var incompatibleModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "meta-llama/llama-3.3-70b-instruct:free",
    "meta-llama/llama-3.1-8b-instruct:free",
    "google/gemma-3-27b-it:free",   // 404 — модель недоступна через /v1/messages
    "google/gemma-3-12b-it:free",
    "google/gemma-3-4b-it:free",
};

var cacheTtl = TimeSpan.FromMinutes(
    configuration.GetValue("Proxy:CacheTtlMinutes", 30));

// Локальный кэш ответов (только не-stream, HTTP 200)
var responseCache = new ConcurrentDictionary<string, CachedResponse>();

// ── Локальные функции для работы с кэшем ──────────────────────────────────────
string NormalizeBodyForCache(JsonObject obj)
{
    var clone = JsonNode.Parse(obj.ToJsonString())!.AsObject();
    clone.Remove("stream");
    clone.Remove("max_tokens");
    clone.Remove("model");
    return clone.ToJsonString();
}

string ComputeCacheKey(string model, string normalizedBody)
{
    var raw = $"{model}|{normalizedBody}";
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    return Convert.ToHexString(hash);
}

app.Urls.Clear();
app.Urls.Add(listenUrl);

// ── GET / ────────────────────────────────────────────────────────────────────
app.MapGet("/", () => Results.Ok(new
{
    name = "anthropic-proxy-csharp",
    status = "ok",
    forced_model = forcedModel,
    fallbacks = fallbackModels,
    cache_ttl_min = cacheTtl.TotalMinutes,
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ── GET /v1/models ───────────────────────────────────────────────────────────
app.MapGet("/v1/models", () => Results.Ok(new
{
    data = new object[]
    {
        new { type = "model", id = "claude-3-5-haiku-latest", display_name = "Claude Haiku (→ OpenRouter)",  created_at = "2026-01-01T00:00:00Z" },
        new { type = "model", id = "claude-sonnet-4-5",       display_name = "Claude Sonnet (→ OpenRouter)", created_at = "2026-01-01T00:00:00Z" },
        new { type = "model", id = "claude-opus-4-1",         display_name = "Claude Opus (→ OpenRouter)",   created_at = "2026-01-01T00:00:00Z" },
        new { type = "model", id = forcedModel,               display_name = "Primary model",                created_at = "2026-01-01T00:00:00Z" },
    },
    first_id = "claude-3-5-haiku-latest",
    has_more = false,
    last_id = forcedModel,
}));

// ── POST /v1/messages ────────────────────────────────────────────────────────
app.MapPost("/v1/messages", async (IHttpClientFactory httpClientFactory, HttpContext httpContext) =>
{
    using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8);
    var requestBody = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(requestBody))
        return Results.BadRequest(new { error = new { type = "invalid_request_error", message = "Request body is empty." } });

    JsonNode? jsonBody;
    try { jsonBody = JsonNode.Parse(requestBody); }
    catch (JsonException ex)
    {
        logger.LogWarning(ex, "Failed to parse incoming JSON.");
        return Results.BadRequest(new { error = new { type = "invalid_request_error", message = ex.Message } });
    }

    if (jsonBody is not JsonObject bodyObject)
        return Results.BadRequest(new { error = new { type = "invalid_request_error", message = "JSON body must be an object." } });

    var incomingModel = bodyObject["model"]?.GetValue<string>() ?? "<missing>";
    var isStream = bodyObject["stream"]?.GetValue<bool>() ?? false;

    if (bodyObject["max_tokens"] is null)
        bodyObject["max_tokens"] = 1024;

    // Проверяем кэш (только для не-stream)
    var normalizedBody = NormalizeBodyForCache(bodyObject);
    var cacheKey = ComputeCacheKey(forcedModel, normalizedBody);

    if (!isStream && responseCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
    {
        logger.LogInformation("Cache HIT for {IncomingModel} (key={Key})", incomingModel, cacheKey[..8]);
        return Results.Content(cached.Body, cached.ContentType, Encoding.UTF8, 200);
    }

    var anthropicVersion = httpContext.Request.Headers.TryGetValue("anthropic-version", out var av)
        ? av.ToString() : "2023-06-01";

    var client = httpClientFactory.CreateClient("openrouter");

    // Строим список моделей для перебора, исключая заведомо несовместимые
    var allCandidates = new[] { forcedModel }
        .Concat(fallbackModels.Where(m => m != forcedModel))
        .Where(m => !incompatibleModels.Contains(m))
        .Distinct()
        .ToArray();

    if (allCandidates.Length == 0)
    {
        logger.LogError("No compatible models available (all are in incompatibleModels list).");
        return Results.Json(
            new { error = new { type = "configuration_error", message = "No compatible models configured." } },
            statusCode: 500);
    }

    HttpResponseMessage? upstreamResponse = null;
    string usedModel = allCandidates[0];

    foreach (var modelCandidate in allCandidates)
    {
        bodyObject["model"] = modelCandidate;

        var upstreamRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{openRouterBaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content = new StringContent(bodyObject.ToJsonString(), Encoding.UTF8, "application/json"),
            Version = new Version(1, 1),
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openRouterApiKey);
        upstreamRequest.Headers.TryAddWithoutValidation("anthropic-version", anthropicVersion);
        upstreamRequest.Headers.TryAddWithoutValidation("HTTP-Referer", "http://127.0.0.1");
        upstreamRequest.Headers.TryAddWithoutValidation("X-Title", "AnthropicProxy");

        logger.LogInformation("Trying [{Model}] for incoming {IncomingModel} (stream={IsStream})",
            modelCandidate, incomingModel, isStream);

        try
        {
            upstreamResponse = await client.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                httpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Network error with model [{Model}].", modelCandidate);
            upstreamResponse = null;
            continue;
        }

        var statusCode = (int)upstreamResponse.StatusCode;
        logger.LogInformation("[{Model}] → HTTP {StatusCode}", modelCandidate, statusCode);

        if (statusCode == 429 || statusCode == 503 || statusCode == 502)
        {
            upstreamResponse.Dispose();
            upstreamResponse = null;
            if (modelCandidate != allCandidates.Last())
                await Task.Delay(300, httpContext.RequestAborted);
            continue;
        }

        // 400 от конкретной модели — добавляем в рантайм-блэклист и пробуем дальше
        if (statusCode == 400)
        {
            logger.LogWarning("[{Model}] returned 400 (incompatible format), skipping.", modelCandidate);
            incompatibleModels.Add(modelCandidate);
            upstreamResponse.Dispose();
            upstreamResponse = null;
            continue;
        }

        // 404 — модель не найдена, убираем из ротации
        if (statusCode == 404)
        {
            logger.LogWarning("[{Model}] returned 404 (model not found), removing from rotation.", modelCandidate);
            incompatibleModels.Add(modelCandidate);
            upstreamResponse.Dispose();
            upstreamResponse = null;
            continue;
        }

        usedModel = modelCandidate;
        break;
    }

    if (upstreamResponse is null)
    {
        logger.LogError("All models exhausted.");
        return Results.Json(
            new { error = new { type = "rate_limit_error", message = "All OpenRouter free models are rate-limited or unavailable. Try again later." } },
            statusCode: 429);
    }

    // Отдаём ответ клиенту
    if (isStream)
    {
        httpContext.Response.StatusCode = (int)upstreamResponse.StatusCode;
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers["Cache-Control"] = "no-cache";
        httpContext.Response.Headers["X-Accel-Buffering"] = "no";
        httpContext.Response.Headers["X-Used-Model"] = usedModel;

        await using var upstreamStream = await upstreamResponse.Content
            .ReadAsStreamAsync(httpContext.RequestAborted);

        try { await upstreamStream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted); }
        catch (OperationCanceledException) { /* клиент отключился */ }

        return Results.Empty;
    }
    else
    {
        await using var upstreamStream = await upstreamResponse.Content
            .ReadAsStreamAsync(httpContext.RequestAborted);
        using var upstreamReader = new StreamReader(upstreamStream, Encoding.UTF8);
        var upstreamBody = await upstreamReader.ReadToEndAsync(httpContext.RequestAborted);

        var contentType = upstreamResponse.Content.Headers.ContentType?.MediaType ?? "application/json";
        var respStatus = (int)upstreamResponse.StatusCode;

        if (respStatus == 200)
        {
            responseCache[cacheKey] = new CachedResponse(upstreamBody, contentType, DateTimeOffset.UtcNow.Add(cacheTtl));

            // Периодическая чистка просроченных записей
            if (responseCache.Count % 100 == 0)
            {
                var now = DateTimeOffset.UtcNow;
                var expired = responseCache.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList();
                foreach (var k in expired) responseCache.TryRemove(k, out _);
            }

            logger.LogInformation("Cache MISS → stored (model={Model}, key={Key})", usedModel, cacheKey[..8]);
        }

        httpContext.Response.Headers["X-Used-Model"] = usedModel;
        return Results.Content(upstreamBody, contentType, Encoding.UTF8, respStatus);
    }
});

app.Run();

record CachedResponse(string Body, string ContentType, DateTimeOffset ExpiresAt);