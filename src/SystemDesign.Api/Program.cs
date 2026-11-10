using SystemDesign.Core;

var builder = WebApplication.CreateBuilder(args);

// One instance per system, shared by every request — exactly like the
// in-memory stores in the design docs; swap these for Redis / SQL Server
// behind the same interfaces when you outgrow one process.
builder.Services.AddSingleton(new UrlShortener());
builder.Services.AddSingleton(new TokenBucketRateLimiter(capacity: 5, refillPerSecond: 1));
builder.Services.AddSingleton(_ => new NotificationDispatcher(new INotificationProvider[]
{
    new InMemoryNotificationProvider(NotificationChannel.Email),
    new InMemoryNotificationProvider(NotificationChannel.Sms),
    new InMemoryNotificationProvider(NotificationChannel.Push)
}));

var app = builder.Build();

app.MapGet("/", () => Results.Json(new
{
    repo = "dotnet-system-design",
    systems = new[]
    {
        "URL shortener — POST /shorten, GET /{code}, GET /stats/{code}",
        "Rate limiter — GET /limited (send X-Api-Key), 5 req burst, 1/s refill",
        "Notification service — POST /notify, GET /notify/{idempotencyKey}, GET /dead-letter"
    },
    docs = new[] { "docs/01-url-shortener.md", "docs/02-rate-limiter.md", "docs/03-notification-service.md" }
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// --- URL shortener -------------------------------------------------------
app.MapPost("/shorten", (ShortenRequest req, UrlShortener shortener, HttpRequest http) =>
{
    try
    {
        var link = shortener.Shorten(req.Url, req.CustomAlias, req.TtlSeconds is null
            ? null
            : TimeSpan.FromSeconds(req.TtlSeconds.Value));
        var baseUrl = $"{http.Scheme}://{http.Host}";
        return Results.Created($"/{link.Code}", new
        {
            link.Code,
            ShortUrl = $"{baseUrl}/{link.Code}",
            link.LongUrl,
            link.ExpiresAt
        });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.Conflict(new { error = ex.Message });
    }
});

app.MapGet("/{code}", (string code, UrlShortener shortener) =>
{
    var link = shortener.Resolve(code);
    // 302, not 301: a permanent redirect would let browsers cache the target
    // forever, so the owner could never change where the code points.
    return link is null ? Results.NotFound(new { error = "Unknown or expired code." }) : Results.Redirect(link.LongUrl);
});

app.MapGet("/stats/{code}", (string code, UrlShortener shortener) =>
{
    var link = shortener.Stats(code);
    return link is null
        ? Results.NotFound(new { error = "Unknown or expired code." })
        : Results.Ok(new { link.Code, link.LongUrl, link.Clicks, link.CreatedAt, link.ExpiresAt });
});

// --- Rate limiter demo ---------------------------------------------------
app.MapGet("/limited", (HttpRequest http, TokenBucketRateLimiter limiter) =>
{
    var key = http.Headers["X-Api-Key"].FirstOrDefault() ?? "anonymous";
    var (allowed, retryAfter) = limiter.TryConsume(key);
    if (!allowed)
        return Results.Json(
            new { error = "Too many requests.", retryAfterSeconds = Math.Ceiling(retryAfter.TotalSeconds) },
            statusCode: StatusCodes.Status429TooManyRequests);

    return Results.Ok(new { message = $"Hello, {key} — request allowed." });
});

// --- Notification service ------------------------------------------------
app.MapPost("/notify", async (NotifyRequest req, NotificationDispatcher dispatcher) =>
{
    if (string.IsNullOrWhiteSpace(req.IdempotencyKey))
        return Results.BadRequest(new { error = "IdempotencyKey is required." });

    var result = await dispatcher.SendAsync(new NotificationRequest(
        req.IdempotencyKey, req.Channel, req.Recipient, req.Subject, req.Body));
    return Results.Accepted($"/notify/{req.IdempotencyKey}", new
    {
        result.Request.IdempotencyKey,
        Status = result.Status.ToString(),
        result.Attempts,
        result.Error
    });
});

app.MapGet("/notify/{key}", (string key, NotificationDispatcher dispatcher) =>
{
    var result = dispatcher.Lookup(key);
    return result is null
        ? Results.NotFound(new { error = "No notification with that idempotency key." })
        : Results.Ok(new
        {
            result.Request.IdempotencyKey,
            result.Request.Channel,
            result.Request.Recipient,
            Status = result.Status.ToString(),
            result.Attempts,
            result.Error
        });
});

app.MapGet("/dead-letter", (NotificationDispatcher dispatcher) =>
    Results.Ok(dispatcher.DeadLetter.Select(d => new
    {
        d.IdempotencyKey,
        d.Channel,
        d.Recipient
    })));

app.Run();

public sealed record ShortenRequest(string Url, string? CustomAlias = null, int? TtlSeconds = null);
public sealed record NotifyRequest(string IdempotencyKey, NotificationChannel Channel, string Recipient, string Subject, string Body);

// Makes the top-level Program visible to WebApplicationFactory in tests.
public partial class Program { }
