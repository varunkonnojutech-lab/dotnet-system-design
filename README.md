# System Design in .NET 8 — URL shortener, rate limiter, notification service

[![Build](https://github.com/varunkonnojutech-lab/dotnet-system-design/actions/workflows/ci.yml/badge.svg)](https://github.com/varunkonnojutech-lab/dotnet-system-design/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Open in GitHub Codespaces](https://github.com/codespaces/badge.svg)](https://codespaces.new/varunkonnojutech-lab/dotnet-system-design)

Three classic system-design interview problems, each with **both** halves of the
answer: a full design doc (requirements → capacity estimation → scaling →
failure modes) **and** a runnable .NET 8 reference implementation with tests
proving the key behaviour. Read the doc, run the code, then explain it out
loud — that's the interview.

No NuGet packages beyond the test SDK, no Redis, no external services. The
in-memory stores are deliberate: the algorithms are the lesson, and the docs
say exactly what replaces them at scale.

## Who is this for

.NET developers preparing for senior/staff interviews, or anyone who has read
a system-design blog post and thought "but how would I actually build the
core of that?" If you can explain why a URL shortener uses 302 instead of
301, you're already ahead of most candidates.

## What you'll learn

- **URL shortener** — counter → Base62 codes (collision-free by construction),
  dedup, custom aliases, TTL expiry, click analytics, and why the read path
  (100:1 read:write) gets a cache before anything else
- **Rate limiter** — a token bucket with refill maths, per-key buckets, and
  the `429` + `Retry-After` contract; plus how Redis + a Lua script makes one
  limit hold across many servers
- **Notification service** — fan-out, idempotency keys (at-least-once delivery
  without duplicates), retry with exponential backoff, and a dead-letter queue
  so failures are visible instead of silent
- **The interview framework** — the four steps every design question follows,
  in [`docs/system-design-interview-framework.md`](docs/system-design-interview-framework.md)

## Quick start

You need .NET 8. Nothing else.

```bash
dotnet test   # 25 tests: Base62, shortener, token bucket, notifications, API end-to-end
dotnet run --project src/SystemDesign.Api   # http://localhost:5091
```

## Try it

```bash
# Shorten a URL — same URL twice returns the same code
curl -s -X POST http://localhost:5091/shorten \
  -H "Content-Type: application/json" \
  -d '{"url":"https://example.com/a-very-long/article?id=42"}'
# {"code":"q0V","shortUrl":"http://localhost:5091/q0V",...}

# Follow the redirect (302, not 301 — see the design doc for why)
curl -si http://localhost:5091/q0V | head -5
# HTTP/1.1 302 Found ... Location: https://example.com/a-very-long/article?id=42

# Clicks counted
curl -s http://localhost:5091/stats/q0V
# {"code":"q0V","clicks":1,...}

# Rate limiter: 5 requests allowed, the 6th is a 429 with retry-after
for i in 1 2 3 4 5 6; do
  curl -s -o /dev/null -w "%{http_code} " -H "X-Api-Key: demo" http://localhost:5091/limited
done
# 200 200 200 200 200 429

# Notification: accepted immediately, status checkable by idempotency key
curl -s -X POST http://localhost:5091/notify \
  -H "Content-Type: application/json" \
  -d '{"idempotencyKey":"welcome-1","channel":0,"recipient":"user@example.com","subject":"Welcome","body":"Hello!"}'
# {"idempotencyKey":"welcome-1","status":"Sent","attempts":1}

# Send the same key again — delivered once, not twice
curl -s http://localhost:5091/notify/welcome-1
# {"idempotencyKey":"welcome-1","status":"Sent","attempts":1}
```

## Endpoints

| Endpoint | What it does |
|---|---|
| `POST /shorten` | `{url, customAlias?, ttlSeconds?}` → `201 {code, shortUrl}` |
| `GET /{code}` | `302` redirect to the original URL (404 when unknown/expired) |
| `GET /stats/{code}` | Clicks, created/expires timestamps |
| `GET /limited` | Rate-limit demo: 5-request burst per `X-Api-Key`, 1 token/sec refill |
| `POST /notify` | `{idempotencyKey, channel, recipient, subject, body}` → `202` |
| `GET /notify/{key}` | Delivery status: Sent / Failed / DeadLettered |
| `GET /dead-letter` | Notifications that exhausted all retries |
| `GET /health` | Liveness check |

## How it works

Everything lives in [`src/SystemDesign.Core/`](src/SystemDesign.Core/):

```csharp
// Codes are a counter encoded in Base62 — no collisions, ever.
var code = Base62.Encode(Interlocked.Increment(ref _counter)); // 100001 → "q0V"

// A token bucket: full at start (burst), refills steadily, never overflows.
var (allowed, retryAfter) = bucket.TryConsume();
if (!allowed) return Results.Json(new { retryAfterSeconds = ... }, statusCode: 429);

// Idempotency: the same key returns the stored result, never a second send.
if (_results.TryGetValue(request.IdempotencyKey, out var existing))
    return existing;
```

The tests ([`tests/SystemDesign.Tests/`](tests/SystemDesign.Tests/)) use a
fake clock so time-based behaviour is deterministic — the bucket refills
exactly 3 tokens when the clock moves 3 seconds, no sleeps, no flaky tests.

## The design docs

| Doc | The one idea to remember |
|---|---|
| [01 — URL shortener](docs/01-url-shortener.md) | Counter → Base62; cache the read path, it's 100:1 |
| [02 — Rate limiter](docs/02-rate-limiter.md) | Token bucket; Redis + Lua to share state across servers |
| [03 — Notification service](docs/03-notification-service.md) | At-least-once + idempotency; DLQ, never silent drops |
| [Interview framework](docs/system-design-interview-framework.md) | Requirements → estimation → design → bottlenecks |

## Next steps

- Read the [interview framework](docs/system-design-interview-framework.md),
  then close it and explain one design out loud in 10 minutes
- Add a fourth design with the same doc template: news feed, chat, or search
  autocomplete are the usual next questions
- See the rate limiter as full ASP.NET Core middleware in
  [rate-limiting-dotnet](https://github.com/varunkonnojutech-lab/rate-limiting-dotnet),
  and the queue/retry patterns at scale in
  [resilience-with-polly](https://github.com/varunkonnojutech-lab/resilience-with-polly)
