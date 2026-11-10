# The System Design Interview Framework

Every design question in this repo — and most real interviews — is the same
four steps. Learn the steps once, then practise them on the three designs.

## Step 1 — Requirements (5 min)

Ask questions before designing. Interviewers grade this more than the diagram.

- **Functional:** what must it *do*? (shorten + redirect; nothing else)
- **Non-functional:** scale? latency? availability? consistency?
- **Out of scope:** say what you're *not* building. Scoping is half the grade.

## Step 2 — Capacity estimation (5 min)

Back-of-the-envelope maths, out loud. You only need three numbers:

- **Throughput:** requests/sec = daily volume ÷ 86,400
- **Storage:** daily volume × bytes/row × retention
- **Bandwidth:** throughput × average payload size

Round aggressively (100M/day ≈ 1,200/sec). The point is showing which part
is big — the answer is usually "reads dominate" or "storage dominates".

## Step 3 — Design (20 min)

Draw the boxes, then defend each one:

1. **API** — the endpoints, with request/response shapes
2. **Data model** — tables, keys, indexes; what the read path queries
3. **The interesting algorithm** — Base62, token bucket, idempotency
4. **Scaling** — cache, read replicas, sharding, queues; in that order
5. **Failure modes** — what breaks, what degrades, what pages someone

## Step 4 — Bottlenecks (5 min)

Name the first thing that breaks at 10× scale and how you'd fix it. "The
read path, at ~100k rps — that's why the cache sits in front of everything"
beats a perfect diagram with no self-criticism.

## How to practise with this repo

| Exercise | Do this |
|---|---|
| Talk through a design | Read a doc, close it, explain it out loud in 10 minutes |
| Defend a trade-off | Pick any "why not X?" line in a doc and argue both sides |
| Break it | Run the API, kill a provider (set `failFirstAttempts: 99`), watch the DLQ fill |
| Extend it | Add a fourth design: news feed, chat, or search autocomplete — same doc template |

## The designs in this repo

| # | System | The one idea to remember |
|---|---|---|
| 1 | [URL shortener](01-url-shortener.md) | Counter → Base62; cache the read path, it's 100:1 |
| 2 | [Rate limiter](02-rate-limiter.md) | Token bucket; Redis + Lua to share state across servers |
| 3 | [Notification service](03-notification-service.md) | At-least-once + idempotency; DLQ, never silent drops |
