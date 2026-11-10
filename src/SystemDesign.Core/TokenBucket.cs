using System.Collections.Concurrent;

namespace SystemDesign.Core;

/// <summary>
/// A single token bucket: starts full (<paramref name="capacity"/> tokens),
/// refills at <paramref name="refillPerSecond"/> tokens per second, and never
/// holds more than <paramref name="capacity"/>.
/// The clock is injected so tests can move time forward deterministically
/// instead of sleeping — the same trick production code uses with
/// <c>TimeProvider</c> in .NET 8.
/// </summary>
public sealed class TokenBucket
{
    private readonly double _capacity;
    private readonly double _refillPerSecond;
    private readonly Func<DateTimeOffset> _clock;
    private double _tokens;
    private DateTimeOffset _lastRefill;
    private readonly object _lock = new();

    public TokenBucket(double capacity, double refillPerSecond, Func<DateTimeOffset>? clock = null)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (refillPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(refillPerSecond));
        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _tokens = capacity;
        _lastRefill = _clock();
    }

    /// <summary>
    /// Tries to spend <paramref name="tokens"/> tokens.
    /// Returns whether the request is allowed and, when denied, how long the
    /// caller should wait before retrying (the value a 429 Retry-After header carries).
    /// </summary>
    public (bool Allowed, TimeSpan RetryAfter) TryConsume(int tokens = 1)
    {
        if (tokens <= 0) throw new ArgumentOutOfRangeException(nameof(tokens));
        lock (_lock)
        {
            RefillLocked();
            if (_tokens >= tokens)
            {
                _tokens -= tokens;
                return (true, TimeSpan.Zero);
            }
            var deficit = tokens - _tokens;
            return (false, TimeSpan.FromSeconds(deficit / _refillPerSecond));
        }
    }

    public double AvailableTokens
    {
        get { lock (_lock) { RefillLocked(); return _tokens; } }
    }

    private void RefillLocked()
    {
        var now = _clock();
        var elapsed = (now - _lastRefill).TotalSeconds;
        if (elapsed <= 0) return;
        _tokens = Math.Min(_capacity, _tokens + elapsed * _refillPerSecond);
        _lastRefill = now;
    }
}

/// <summary>
/// Per-key token buckets — the shape a real API gateway uses: every API key
/// (or user, or tenant) gets its own bucket, so one greedy caller can't
/// starve everyone else. In production the buckets live in Redis so every
/// server sees the same counts; the algorithm is identical.
/// </summary>
public sealed class TokenBucketRateLimiter
{
    private readonly ConcurrentDictionary<string, TokenBucket> _buckets = new();
    private readonly double _capacity;
    private readonly double _refillPerSecond;
    private readonly Func<DateTimeOffset> _clock;

    public TokenBucketRateLimiter(double capacity, double refillPerSecond, Func<DateTimeOffset>? clock = null)
    {
        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public (bool Allowed, TimeSpan RetryAfter) TryConsume(string key, int tokens = 1) =>
        _buckets.GetOrAdd(key, _ => new TokenBucket(_capacity, _refillPerSecond, _clock))
            .TryConsume(tokens);
}
