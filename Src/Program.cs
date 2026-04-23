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

// ── Локальные функции для работы с кэшем ─────────────────────────────────────
string ComputeCacheKey(string model, string normalizedBody)
{
    var raw  = $"{model}|{normalizedBody}";
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    return Convert.ToHexString(hash);
}

string NormalizeBodyForCache(JsonObject obj)
{
    var clone = JsonNode.Parse(obj.ToJsonString())!.AsObject();
    clone.Remove("stream");
    clone.Remove("max_tokens");
    clone.Remove("model");
    return clone.ToJsonString();
}

var listenUrl          = configuration["Proxy:ListenUrl"]          ?? "http://127.0.0.1:3000";
var openRouterBaseUrl  = configuration["Proxy:OpenRouterBaseUrl"]  ?? "https://openrouter.ai/api";
var forcedModel        = configuration["Proxy:ForcedModel"]        ?? "qwen/qwen3-coder:free";

// Список фолбэк-моделей: если основная даёт 429, пробуем следующую
var fallbackModels = configuration
    .GetSection("Proxy:FallbackModels")
    .Get<string[]>()
    ?? [
        "qwen/qwen3-coder:free",
        "meta-llama/llama-3.3-70b-instruct:free",
        "nvidia/nemotron-3-super-120b-a12b:free",
        "google/gemma-3-27b-it:free",
    ];

var openRouterApiKey =
    Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ??
    configuration["Proxy:OpenRouterApiKey"];

if (string.IsNullOrWhiteSpace(openRouterApiKey))
    throw new InvalidOperationException("OPENROUTER_API_KEY is not set.");

// ── Локальный кэш ответов ────────────────────────────────────────────────────
// Кэшируем только не-stream ответы с кодом 200.
// Ключ = SHA256(model + тело запроса без stream/max_tokens).
var responseCache = new ConcurrentDictionary<string, CachedResponse>();
var cacheTtl      = TimeSpan.FromMinutes(
    configuration.GetValue("Proxy:CacheTtlMinutes", 30));

app.Urls.Clear();
app.Urls.Add(listenUrl);

// ── /  ────────────────────────────────────────────────────────────────────────
app.MapGet("/", () => Results.Ok(new
{
    name          = "anthropic-proxy-csharp",
    status        = "ok",
    forced_model  = forcedModel,
    fallbacks     = fallbackModels,
    cache_ttl_min = cacheTtl.TotalMinutes,
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// ── /v1/models ───────────────────────────────────────────────────────────────
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
    last_id  = forcedModel,
}));

// ── /v1/messages ─────────────────────────────────────────────────────────────
app.MapPost("/v1/messages", async (IHttpClientFactory httpClientFactory, HttpContext httpContext) =>
{
    // 1. Читаем тело
    using var reader      = new StreamReader(httpContext.Request.Body, Encoding.UTF8);
    var       requestBody = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(requestBody))
        return Results.BadRequest(new { error = new { type = "invalid_request_error", message = "Request body is empty." } });

    JsonNode? jsonBody;
    try   { jsonBody = JsonNode.Parse(requestBody); }
    catch (JsonException ex)
    {
        logger.LogWarning(ex, "Failed to parse incoming JSON.");
        return Results.BadRequest(new { error = new { type = "invalid_request_error", message = ex.Message } });
    }

    if (jsonBody is not JsonObject bodyObject)
        return Results.BadRequest(new { error = new { type = "invalid_request_error", message = "JSON body must be an object." } });

    var incomingModel = bodyObject["model"]?.GetValue<string>() ?? "<missing>";
    var isStream      = bodyObject["stream"]?.GetValue<bool>() ?? false;

    if (bodyObject["max_tokens"] is null)
        bodyObject["max_tokens"] = 1024;

    // 2. Проверяем кэш (только для не-stream запросов)
    var normalizedBody = NormalizeBodyForCache(bodyObject);
    var cacheKey       = ComputeCacheKey(forcedModel, normalizedBody);

    if (!isStream && responseCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
    {
        logger.LogInformation("Cache HIT for model {IncomingModel} (key={Key})", incomingModel, cacheKey[..8]);
        return Results.Content(cached.Body, cached.ContentType, Encoding.UTF8, 200);
    }

    // 3. Anthropic-version из оригинального запроса
    var anthropicVersion = httpContext.Request.Headers.TryGetValue("anthropic-version", out var av)
        ? av.ToString()
        : "2023-06-01";

    var client = httpClientFactory.CreateClient("openrouter");

    // 4. Перебираем модели: основная + фолбэки
    var modelsToTry = new[] { forcedModel }.Concat(fallbackModels.Where(m => m != forcedModel)).ToArray();

    HttpResponseMessage? upstreamResponse = null;
    string              usedModel         = forcedModel;

    foreach (var modelCandidate in modelsToTry)
    {
        bodyObject["model"] = modelCandidate;

        var upstreamRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{openRouterBaseUrl.TrimEnd('/')}/v1/messages")
        {
            Content       = new StringContent(bodyObject.ToJsonString(), Encoding.UTF8, "application/json"),
            Version       = new Version(1, 1),
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };

        upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", openRouterApiKey);
        upstreamRequest.Headers.TryAddWithoutValidation("anthropic-version", anthropicVersion);
        upstreamRequest.Headers.TryAddWithoutValidation("HTTP-Referer", "http://127.0.0.1");
        upstreamRequest.Headers.TryAddWithoutValidation("X-Title", "AnthropicProxy");

        logger.LogInformation("Trying model {Model} for incoming {IncomingModel} (stream={IsStream})",
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
            logger.LogError(ex, "Network error reaching OpenRouter with model {Model}.", modelCandidate);
            upstreamResponse = null;
            continue;
        }

        var statusCode = (int)upstreamResponse.StatusCode;
        logger.LogInformation("OpenRouter [{Model}] → HTTP {StatusCode}", modelCandidate, statusCode);

        if (statusCode == 429 || statusCode == 503 || statusCode == 502)
        {
            // Rate-limited или недоступна — пробуем следующую модель
            upstreamResponse.Dispose();
            upstreamResponse = null;

            if (modelCandidate != modelsToTry.Last())
            {
                // Небольшая пауза перед следующей попыткой
                await Task.Delay(300, httpContext.RequestAborted);
            }
            continue;
        }

        usedModel = modelCandidate;
        break; // Успех (или ошибка другого рода — отдаём клиенту как есть)
    }

    // 5. Все модели исчерпаны
    if (upstreamResponse is null)
    {
        logger.LogError("All models exhausted (all returned 429/503/network error).");
        return Results.Json(
            new { error = new { type = "rate_limit_error", message = "All OpenRouter free models are rate-limited. Try again later." } },
            statusCode: 429);
    }

    // 6. Отдаём ответ клиенту
    if (isStream)
    {
        // SSE: пробрасываем поток напрямую
        httpContext.Response.StatusCode  = (int)upstreamResponse.StatusCode;
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers["Cache-Control"]    = "no-cache";
        httpContext.Response.Headers["X-Accel-Buffering"]= "no";
        httpContext.Response.Headers["X-Used-Model"]    = usedModel;

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
        var statusCode  = (int)upstreamResponse.StatusCode;

        // 7. Кладём в кэш только успешные ответы
        if (statusCode == 200)
        {
            var entry = new CachedResponse(upstreamBody, contentType, DateTimeOffset.UtcNow.Add(cacheTtl));
            responseCache[cacheKey] = entry;

            // Чистим протухшие записи (не чаще раза в 100 запросов)
            if (responseCache.Count % 100 == 0)
            {
                var now     = DateTimeOffset.UtcNow;
                var expired = responseCache.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList();
                foreach (var k in expired) responseCache.TryRemove(k, out _);
            }

            logger.LogInformation("Cache MISS → stored (model={Model}, key={Key}, ttl={Ttl}min)",
                usedModel, cacheKey[..8], cacheTtl.TotalMinutes);
        }

        httpContext.Response.Headers["X-Used-Model"] = usedModel;

        return Results.Content(upstreamBody, contentType, Encoding.UTF8, statusCode);
    }
});

app.Run();

record CachedResponse(string Body, string ContentType, DateTimeOffset ExpiresAt);