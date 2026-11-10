# Design 3 — Notification Service (like a multi-channel notifier)

The reference implementation in `src/` is `NotificationDispatcher` with
pluggable providers (email / SMS / push), idempotency, retries with backoff,
and a dead-letter queue. The API exposes it at `POST /notify`.

## 1. Requirements

**Functional**
- Send notifications over email, SMS, and push from one API
- Templates ("Welcome, {name}") with per-user variables
- User preferences: which channels, quiet hours, opt-outs
- Delivery status per notification: sent / failed / dead-lettered

**Non-functional**
- 10M notifications/day (~120/sec average, 10× peaks)
- **At-least-once delivery, never lost** — a missed password-reset email is
  worse than a duplicate
- No duplicate deliveries for retried requests (idempotency)
- A failing provider (SendGrid down) must not block other channels

**Out of scope:** the message content pipeline (marketing campaigns),
in-app notification inbox, provider billing.

## 2. Capacity estimation

| Number | Maths |
|---|---|
| Throughput | 10M/day ≈ **120/sec avg**, ~1,200/sec peak |
| Storage (1 KB/notification, 90-day retention) | 10M × 90 × 1 KB ≈ **900 GB** |
| Fan-out | One event → up to 3 channels; a "new follower" event for a celebrity → 1M recipients |

The fan-out row is the interesting one: sending to a million followers is a
different problem from sending one email, and the design below handles both.

## 3. The pipeline

```
API → Validate → Queue (durable) → Workers → Template render → Provider → Status store
                       ↓ (poison)                    ↓ (fail)
                   Dead-letter queue              Retry with backoff
```

1. **API validates** (recipient, template, idempotency key) and writes to a
   **durable queue** (SQS / Service Bus / Kafka) — the request returns `202`
   immediately. The queue is what makes "never lost" possible: if the process
   crashes, the notification is still in the queue.
2. **Workers** pull from the queue, render the template, check user
   preferences (opted out of SMS? quiet hours?), and call the provider.
3. **Status** is written per notification so `GET /notify/{key}` can answer
   "did it go out?"

## 4. The three behaviours that matter

### Idempotency — never deliver twice

Every request carries an `IdempotencyKey`. The dispatcher stores the result
under that key; a retry of the same request returns the stored result instead
of sending again. This is what makes at-least-once safe: the queue can redeliver
a message after a crash, and the user still gets exactly one email.

The test proves it: send the same key twice, the provider records one send.

### Retry with exponential backoff

A provider failure is usually transient (a 30-second outage, a rate limit).
Retry after 100 ms, 200 ms, 400 ms… up to `MaxAttempts`. Backoff matters:
retrying instantly turns one outage into a self-inflicted DDoS on the provider.

### Dead-letter queue (DLQ)

After the last attempt the notification goes to the dead-letter list instead
of vanishing. An operator can inspect it, fix the cause (bad phone number?
provider credentials expired?), and replay it. **A notification service that
silently drops messages is a bug, not a design.**

## 5. Fan-out: one event, a million recipients

- **Per-recipient rows:** a "new post" event for a celebrity with 1M followers
  becomes 1M queue messages. Simple, durable, slow to enqueue (batch it).
- **Push model (used here):** enqueue on publish; workers drain at provider
  speed. If the provider allows 100 SMS/sec and you have 1M to send, that's
  ~3 hours — say so in the interview, and offer the alternative:
- **Pull model:** store "there is a new post" once; each user's app fetches it
  on open. No fan-out at all — this is how feeds scale, and it's the right
  answer when delivery can be lazy.

## 6. Scaling and failure modes

- **Scale workers, not the API:** the queue absorbs peaks; add workers until
  the provider's rate limit is the bottleneck, then negotiate a higher limit.
- **Provider rate limits:** each provider (SendGrid, Twilio, FCM) has its own
  quota — throttle per provider, and queue per provider so a slow SMS provider
  doesn't block email.
- **Provider down:** retry + backoff, then DLQ. Never fail the API request
  because a downstream provider is sick — the queue already accepted the work.
- **Ordering:** notifications to the same user should arrive in order
  (password-reset *after* welcome). Use a per-user FIFO queue or a sequence
  number per recipient; global ordering is neither possible nor needed.
- **Template rendering failures:** a missing `{name}` variable must fail the
  *render*, not the send — validate at enqueue time, not at 2 AM in a worker.

## 7. Interview talking points

- "At-least-once or exactly-once?" — exactly-once delivery is impossible in
  a distributed system; **at-least-once + idempotency** is the honest answer.
- "What happens when SendGrid is down?" — queue holds the work, retries with
  backoff, DLQ after MaxAttempts. The API never fails the caller.
- "How do you send to a million followers?" — per-recipient queue messages,
  or pull model if lazy delivery is acceptable.
- "How do you avoid waking users at 3 AM?" — preferences + quiet hours checked
  at send time, not at enqueue time (preferences can change in between).
