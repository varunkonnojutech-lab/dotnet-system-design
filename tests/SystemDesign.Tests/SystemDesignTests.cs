using System.Net;
using System.Net.Http.Json;
using SystemDesign.Core;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SystemDesign.Tests;

/// <summary>A clock the test controls — move time forward instead of sleeping.</summary>
public sealed class FakeClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 10, 7, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => Now += by;
}

public sealed class Base62Tests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(61, "Z")]
    [InlineData(62, "10")]
    [InlineData(3844, "100")] // 62^2
    public void Encode_KnownValues(long value, string expected) =>
        Assert.Equal(expected, Base62.Encode(value));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(123456789)]
    [InlineData(long.MaxValue / 2)]
    public void Decode_RoundTrips(long value) =>
        Assert.Equal(value, Base62.Decode(Base62.Encode(value)));

    [Fact]
    public void Decode_InvalidCharacter_Throws() =>
        Assert.Throws<ArgumentException>(() => Base62.Decode("ab!"));
}

public sealed class UrlShortenerTests
{
    [Fact]
    public void Shorten_Then_Resolve_ReturnsLongUrl_AndCountsClicks()
    {
        var shortener = new UrlShortener();
        var link = shortener.Shorten("https://example.com/a-very-long/article?id=42");

        var resolved = shortener.Resolve(link.Code);
        Assert.NotNull(resolved);
        Assert.Equal("https://example.com/a-very-long/article?id=42", resolved!.LongUrl);
        Assert.Equal(1, resolved.Clicks);
        shortener.Resolve(link.Code);
        Assert.Equal(2, shortener.Stats(link.Code)!.Clicks);
    }

    [Fact]
    public void SameUrl_ShortenedTwice_ReturnsSameCode()
    {
        var shortener = new UrlShortener();
        var first = shortener.Shorten("https://example.com/same");
        var second = shortener.Shorten("https://example.com/same");
        Assert.Equal(first.Code, second.Code);
        Assert.Equal(1, shortener.Count);
    }

    [Fact]
    public void ThousandDistinctUrls_GetThousandDistinctCodes()
    {
        var shortener = new UrlShortener();
        var codes = Enumerable.Range(0, 1000)
            .Select(i => shortener.Shorten($"https://example.com/page/{i}").Code)
            .ToHashSet();
        Assert.Equal(1000, codes.Count);
    }

    [Fact]
    public void CustomAlias_Works_AndSecondUseConflicts()
    {
        var shortener = new UrlShortener();
        var link = shortener.Shorten("https://example.com/landing", customAlias: "launch");
        Assert.Equal("launch", link.Code);
        Assert.Throws<InvalidOperationException>(() =>
            shortener.Shorten("https://example.com/other", customAlias: "launch"));
    }

    [Fact]
    public void NonHttpUrl_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            new UrlShortener().Shorten("ftp://example.com/file"));

    [Fact]
    public void ExpiredLink_DoesNotResolve()
    {
        var clock = new FakeClock();
        var shortener = new UrlShortener(() => clock.Now);
        var link = shortener.Shorten("https://example.com/temp", ttl: TimeSpan.FromMinutes(5));

        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.NotNull(shortener.Resolve(link.Code)); // still alive at 4 min
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(shortener.Resolve(link.Code));    // gone at 6 min
    }
}

public sealed class TokenBucketTests
{
    [Fact]
    public void Bucket_AllowsBurst_ThenDenies_WithRetryAfter()
    {
        var clock = new FakeClock();
        var bucket = new TokenBucket(capacity: 5, refillPerSecond: 1, () => clock.Now);

        for (var i = 0; i < 5; i++)
            Assert.True(bucket.TryConsume().Allowed);

        var denied = bucket.TryConsume();
        Assert.False(denied.Allowed);
        Assert.True(denied.RetryAfter > TimeSpan.Zero);
    }

    [Fact]
    public void Bucket_RefillsOverTime()
    {
        var clock = new FakeClock();
        var bucket = new TokenBucket(capacity: 5, refillPerSecond: 1, () => clock.Now);

        for (var i = 0; i < 5; i++) bucket.TryConsume();
        Assert.False(bucket.TryConsume().Allowed);

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(3, bucket.AvailableTokens, precision: 5);
        Assert.True(bucket.TryConsume().Allowed);
    }

    [Fact]
    public void PerKeyBuckets_AreIndependent()
    {
        var clock = new FakeClock();
        var limiter = new TokenBucketRateLimiter(capacity: 2, refillPerSecond: 1, () => clock.Now);

        Assert.True(limiter.TryConsume("alice").Allowed);
        Assert.True(limiter.TryConsume("alice").Allowed);
        Assert.False(limiter.TryConsume("alice").Allowed);
        Assert.True(limiter.TryConsume("bob").Allowed); // bob untouched by alice's burst
    }
}

public sealed class NotificationDispatcherTests
{
    private static NotificationRequest Req(string key, NotificationChannel channel = NotificationChannel.Email) =>
        new(key, channel, "user@example.com", "Hello", "Test message");

    [Fact]
    public async Task Send_DeliversOnce_AndRecordsIt()
    {
        var provider = new InMemoryNotificationProvider(NotificationChannel.Email);
        var dispatcher = new NotificationDispatcher(new[] { provider }, backoff: _ => TimeSpan.Zero);

        var result = await dispatcher.SendAsync(Req("k1"));
        Assert.Equal(NotificationStatus.Sent, result.Status);
        Assert.Equal(1, result.Attempts);
        Assert.Single(provider.Sent);
    }

    [Fact]
    public async Task SameIdempotencyKey_Twice_DeliversOnce()
    {
        var provider = new InMemoryNotificationProvider(NotificationChannel.Email);
        var dispatcher = new NotificationDispatcher(new[] { provider }, backoff: _ => TimeSpan.Zero);

        await dispatcher.SendAsync(Req("k1"));
        var second = await dispatcher.SendAsync(Req("k1"));

        Assert.Equal(NotificationStatus.Sent, second.Status);
        Assert.Single(provider.Sent); // the retry was a no-op, not a second email
    }

    [Fact]
    public async Task TransientFailure_Retries_ThenSucceeds()
    {
        var provider = new InMemoryNotificationProvider(NotificationChannel.Sms, failFirstAttempts: 2);
        var dispatcher = new NotificationDispatcher(new[] { provider }, backoff: _ => TimeSpan.Zero);

        var result = await dispatcher.SendAsync(Req("k2", NotificationChannel.Sms));
        Assert.Equal(NotificationStatus.Sent, result.Status);
        Assert.Equal(3, result.Attempts);
        Assert.Single(provider.Sent);
    }

    [Fact]
    public async Task PermanentFailure_LandsInDeadLetter()
    {
        var provider = new InMemoryNotificationProvider(NotificationChannel.Push, failFirstAttempts: 99);
        var dispatcher = new NotificationDispatcher(new[] { provider }, backoff: _ => TimeSpan.Zero);

        var result = await dispatcher.SendAsync(Req("k3", NotificationChannel.Push));
        Assert.Equal(NotificationStatus.DeadLettered, result.Status);
        Assert.Equal(3, result.Attempts); // MaxAttempts
        Assert.Empty(provider.Sent);
        Assert.Single(dispatcher.DeadLetter);
        Assert.Equal("k3", dispatcher.DeadLetter.Single().IdempotencyKey);
    }
}

public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Shorten_Then_Redirect_WorksEndToEnd()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var shorten = await client.PostAsJsonAsync("/shorten",
            new { Url = "https://example.com/some/long/path?x=1" });
        Assert.Equal(HttpStatusCode.Created, shorten.StatusCode);
        var body = await shorten.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var code = body!["code"]!.ToString()!;

        var redirect = await client.GetAsync($"/{code}");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal("https://example.com/some/long/path?x=1", redirect.Headers.Location!.ToString());

        var stats = await client.GetAsync($"/stats/{code}");
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
    }

    [Fact]
    public async Task LimitedEndpoint_AllowsBurst_ThenReturns429()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "api-test-key-" + Guid.NewGuid());

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            codes.Add((await client.GetAsync("/limited")).StatusCode);

        Assert.Equal(5, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
    }

    [Fact]
    public async Task Notify_IsAccepted_AndStatusIsSent()
    {
        var client = _factory.CreateClient();
        var key = "api-" + Guid.NewGuid();

        var sent = await client.PostAsJsonAsync("/notify", new
        {
            IdempotencyKey = key,
            Channel = 0, // Email
            Recipient = "user@example.com",
            Subject = "Welcome",
            Body = "Hello!"
        });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);

        var status = await client.GetAsync($"/notify/{key}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var body = await status.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("Sent", body!["status"]!.ToString());
    }
}
