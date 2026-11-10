using System.Collections.Concurrent;

namespace SystemDesign.Core;

/// <summary>
/// One shortened link. Clicks is a plain field (not a property) so it can be
/// incremented lock-free with <see cref="Interlocked.Increment"/>.
/// </summary>
public sealed class ShortUrl
{
    public required string Code { get; init; }
    public required string LongUrl { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public long Clicks;
}

/// <summary>
/// In-memory URL shortener. The design decision that matters most lives here:
/// codes come from a single monotonic counter encoded in Base62, so two
/// different URLs can never collide and the same URL always maps to the same
/// code (deduplication via <see cref="_byUrl"/>).
///
/// In production the counter becomes a distributed id generator (see
/// docs/01-url-shortener.md — Twitter Snowflake / a dedicated id service),
/// the dictionaries become a database table + cache, and this class stays
/// exactly the same shape.
/// </summary>
public sealed class UrlShortener
{
    private readonly ConcurrentDictionary<string, ShortUrl> _byCode = new();
    private readonly ConcurrentDictionary<string, string> _byUrl = new();
    private long _counter = 100_000; // start above 0 so the first codes are 3 chars
    private readonly Func<DateTimeOffset> _clock;

    public UrlShortener(Func<DateTimeOffset>? clock = null) =>
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>
    /// Shortens <paramref name="longUrl"/>. Reuses the existing code when the
    /// URL was already shortened (unless a custom alias is requested).
    /// Throws <see cref="ArgumentException"/> for non-http(s) URLs and
    /// <see cref="InvalidOperationException"/> when a custom alias is taken.
    /// </summary>
    public ShortUrl Shorten(string longUrl, string? customAlias = null, TimeSpan? ttl = null)
    {
        if (!Uri.TryCreate(longUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            throw new ArgumentException("Must be an absolute http(s) URL.", nameof(longUrl));

        if (customAlias is not null)
        {
            if (customAlias.Length is < 1 or > 64
                || customAlias.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
                throw new ArgumentException(
                    "Custom alias must be 1-64 characters of letters, digits, '-' or '_'.",
                    nameof(customAlias));

            var now = _clock();
            var alias = new ShortUrl
            {
                Code = customAlias,
                LongUrl = longUrl,
                CreatedAt = now,
                ExpiresAt = ttl is null ? null : now + ttl.Value
            };
            if (!_byCode.TryAdd(customAlias, alias))
                throw new InvalidOperationException($"Alias '{customAlias}' is already taken.");
            _byUrl.TryAdd(longUrl, customAlias);
            return alias;
        }

        if (_byUrl.TryGetValue(longUrl, out var existingCode)
            && _byCode.TryGetValue(existingCode, out var existing)
            && !IsExpired(existing))
            return existing;

        var code = Base62.Encode(Interlocked.Increment(ref _counter));
        var shortened = new ShortUrl
        {
            Code = code,
            LongUrl = longUrl,
            CreatedAt = _clock(),
            ExpiresAt = ttl is null ? null : _clock() + ttl.Value
        };
        _byCode[code] = shortened;
        _byUrl[longUrl] = code;
        return shortened;
    }

    /// <summary>Resolves a code to its link and counts a click. Null when unknown or expired.</summary>
    public ShortUrl? Resolve(string code)
    {
        if (!_byCode.TryGetValue(code, out var link) || IsExpired(link))
            return null;
        Interlocked.Increment(ref link.Clicks);
        return link;
    }

    public ShortUrl? Stats(string code) =>
        _byCode.TryGetValue(code, out var link) && !IsExpired(link) ? link : null;

    public int Count => _byCode.Count;

    private bool IsExpired(ShortUrl link) =>
        link.ExpiresAt is not null && _clock() > link.ExpiresAt.Value;
}
