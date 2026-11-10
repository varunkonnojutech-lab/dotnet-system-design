# Design 2 — Rate Limiter (like an API gateway quota)

The reference implementation in `src/` is a token bucket (`TokenBucket`,
`TokenBucketRateLimiter`) with a live demo at `GET /limited` — send
`X-Api-Key` and watch the 6th request in a burst come back `429`.

## 1. Requirements

**Functional**
- Limit requests per client (API key / user / IP) — e.g. 100 requests/minute
- Return `429 Too Many Requests` with a `Retry-After` header when over the limit
- Different limits for different plans (free vs paid)

**Non-functional**
- The limiter must be **faster than the thing it protects** — a slow limiter
  is worse than no limiter
- Accurate enough: small over-admission is fine, large over-admission is not
- Works across many servers (a limit of 100/min must mean 100/min *total*,
  not 100/min *per server*)
- Fail safe: if the limiter itself is down, let traffic through (fail open)
  and page someone

**Out of scope:** billing/metering per request, abuse detection, WAF rules.

## 2. The four algorithms

| Algorithm | How it works | Strength | Weakness |
|---|---|---|---|
| **Fixed window** | Counter per window (e.g. 100/min), reset at the boundary | Dead simple | Burst at the boundary: 100 at 11:59:59 + 100 at 12:00:00 |
| **Sliding window log** | Store every request timestamp, count the last N seconds | Exact | Memory grows with traffic |
| **Sliding window counter** | Weighted blend of current + previous window counters | Smooth, cheap | Approximate (usually fine) |
| **Token bucket (used here)** | Bucket refills at a steady rate, bursts allowed up to capacity | Burst-friendly, cheap, intuitive | Needs a refill calculation per check |

**Leaky bucket** is token bucket's twin: requests queue and leak out at a
fixed rate. It smooths bursts instead of allowing them — good for protecting a
slow downstream, bad for user-facing APIs where a burst is legitimate.

For interviews: **token bucket is the default answer**. Fixed window is the
"simple but flawed" answer you mention and move past.

## 3. Capacity estimation

- 10,000 requests/sec across the API, 1M active API keys
- State per key: ~100 bytes (count + window timestamps) → ~100 MB in memory
- A limiter check must cost **< 1 ms** — it's on the hot path of every request

## 4. How the token bucket works

```
capacity = 100 tokens, refill = 100 tokens / 60s
every request spends 1 token
bucket refills continuously: tokens = min(capacity, tokens + elapsed × rate)
```

A fresh client gets a full bucket (burst of 100), then a steady 100/min.
`TryConsume` returns `(allowed, retryAfter)` — the retry-after is what the
`429` response carries so well-behaved clients back off instead of hammering.

The tests prove the exact behaviour with a fake clock: burst of 5 allowed,
6th denied, 3 seconds later 3 tokens are back. No sleeps, no flaky tests.

## 5. Distributed: one limit across many servers

In-memory buckets (what this repo runs) are per-process. With 10 servers
behind a load balancer, "100/min" silently becomes "100/min per server".
Fix it with a shared store:

**Redis + Lua script (the standard answer):**
```lua
-- atomic: refill, check, decrement — one round-trip, no race
local tokens = tonumber(redis.call('get', KEYS[1]) or ARGV[1])
local now = tonumber(ARGV[2])
-- ... refill maths ...
if tokens >= 1 then redis.call('set', KEYS[1], tokens - 1) return 1 end
return 0
```
The Lua script runs atomically on the Redis server, so two app servers can
never both spend the last token.

**Alternatives:** a dedicated rate-limit service (extra hop, extra failure
mode); sticky sessions (breaks the moment a server restarts — don't).

**Key design:** partition by something that identifies the *caller* — API key,
user id, tenant id. Limiting by client IP breaks behind a load balancer or
NAT, where hundreds of users share one IP. (The same trap is called out in
[rate-limiting-dotnet](https://github.com/varunkonnojutech-lab/rate-limiting-dotnet).)

## 6. Where the limiter lives

```
Client → CDN / Load balancer → Rate limiter → App servers → Downstream
```

- **At the gateway** (best): rejects bad traffic before it costs you compute.
- **As middleware** (this repo's demo): fine for one service, see the
  `rate-limiting-dotnet` repo for the full ASP.NET Core middleware version.
- **At the client** (SDKs): polite, unenforceable — never the only layer.

## 7. Failure modes

- **Redis down:** fail **open** (allow traffic, alert loudly). A down limiter
  must never take down the API it protects.
- **Clock skew between servers:** use Redis server time (`TIME`) inside the
  Lua script, never the app server's clock.
- **Hot key:** one abusive key hammering Redis — shard keys across Redis
  nodes, and add a local in-memory pre-filter for obviously abusive keys.
- **Retry storms:** a client that retries instantly on 429 multiplies load.
  `Retry-After` + exponential backoff on the client side is part of the design.

## 8. Interview talking points

- "Fixed window vs token bucket?" — boundary bursts; token bucket is the
  default, fixed window is the simple version.
- "How do you make it work on 10 servers?" — Redis + atomic Lua, never
  per-process counters.
- "What if Redis goes down?" — fail open, alert, degrade gracefully.
- "Per-IP or per-key?" — per-key; IPs are shared behind NATs and load balancers.
