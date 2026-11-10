using System.Collections.Concurrent;

namespace SystemDesign.Core;

public enum NotificationChannel { Email, Sms, Push }

public enum NotificationStatus { Sent, Failed, DeadLettered }

public sealed record NotificationRequest(
    string IdempotencyKey,
    NotificationChannel Channel,
    string Recipient,
    string Subject,
    string Body);

public sealed record NotificationResult(
    NotificationRequest Request,
    NotificationStatus Status,
    int Attempts,
    string? Error = null);

/// <summary>
/// One delivery channel. Production implementations call SendGrid / Twilio /
/// Firebase here; the in-memory one records what it "sent" so tests and the
/// demo can inspect it, and can be told to fail the first N attempts to
/// exercise the retry path.
/// </summary>
public interface INotificationProvider
{
    NotificationChannel Channel { get; }
    Task SendAsync(NotificationRequest request, CancellationToken ct = default);
}

public sealed class InMemoryNotificationProvider : INotificationProvider
{
    private int _failuresRemaining;
    private readonly object _lock = new();

    public NotificationChannel Channel { get; }
    public List<NotificationRequest> Sent { get; } = new();

    public InMemoryNotificationProvider(NotificationChannel channel, int failFirstAttempts = 0)
    {
        Channel = channel;
        _failuresRemaining = failFirstAttempts;
    }

    public Task SendAsync(NotificationRequest request, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new InvalidOperationException($"Simulated {Channel} provider failure.");
            }
            Sent.Add(request);
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Fan-out + retry + dead-letter, the three behaviours every notification
/// service needs and the three most interviewers ask about:
/// <list type="bullet">
///   <item><b>Idempotency:</b> the same <c>IdempotencyKey</c> is never delivered
///   twice, even if the client retries the request. This is what makes
///   at-least-once delivery safe.</item>
///   <item><b>Retry with backoff:</b> a failing provider is retried up to
///   <c>MaxAttempts</c> times with exponential backoff, so a brief outage
///   doesn't lose the notification.</item>
///   <item><b>Dead letter:</b> after the last attempt the notification lands
///   in the dead-letter list instead of vanishing — an operator (or a
///   replayer) can inspect and resend it.</item>
/// </list>
/// In production this sits behind a durable queue (SQS / Service Bus / Kafka)
/// so a crash doesn't lose queued work; the delivery logic is the same.
/// </summary>
public sealed class NotificationDispatcher
{
    private readonly IReadOnlyDictionary<NotificationChannel, INotificationProvider> _providers;
    private readonly ConcurrentDictionary<string, NotificationResult> _results = new();
    private readonly ConcurrentQueue<NotificationRequest> _deadLetter = new();
    private readonly Func<int, TimeSpan> _backoff;

    public int MaxAttempts { get; }

    public NotificationDispatcher(
        IEnumerable<INotificationProvider> providers,
        int maxAttempts = 3,
        Func<int, TimeSpan>? backoff = null)
    {
        _providers = providers.ToDictionary(p => p.Channel);
        MaxAttempts = maxAttempts;
        _backoff = backoff ?? (attempt => TimeSpan.FromMilliseconds(100 * (1 << (attempt - 1))));
    }

    public IReadOnlyCollection<NotificationRequest> DeadLetter => _deadLetter.ToArray();

    /// <summary>Sends a notification, honouring idempotency and retries.</summary>
    public async Task<NotificationResult> SendAsync(NotificationRequest request, CancellationToken ct = default)
    {
        if (_results.TryGetValue(request.IdempotencyKey, out var existing))
            return existing; // already processed — never deliver twice

        if (!_providers.TryGetValue(request.Channel, out var provider))
        {
            var unsupported = new NotificationResult(request, NotificationStatus.DeadLettered, 0,
                $"No provider registered for {request.Channel}.");
            _results[request.IdempotencyKey] = unsupported;
            _deadLetter.Enqueue(request);
            return unsupported;
        }

        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await provider.SendAsync(request, ct);
                var sent = new NotificationResult(request, NotificationStatus.Sent, attempt);
                _results[request.IdempotencyKey] = sent;
                return sent;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                lastError = ex;
                await Task.Delay(_backoff(attempt), ct);
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        var dead = new NotificationResult(request, NotificationStatus.DeadLettered, MaxAttempts,
            lastError?.Message);
        _results[request.IdempotencyKey] = dead;
        _deadLetter.Enqueue(request);
        return dead;
    }

    public NotificationResult? Lookup(string idempotencyKey) =>
        _results.TryGetValue(idempotencyKey, out var result) ? result : null;
}
