# Design 1 — URL Shortener (like bit.ly / TinyURL)

The classic first system-design question. The reference implementation in
`src/` is the runnable core: `Base62`, `UrlShortener`, and the `/shorten` +
`/{code}` endpoints in the API.

## 1. Requirements

**Functional**
- `POST /shorten` — long URL in, short code out (`sho.rt/aB3x9Q`)
- `GET /{code}` — redirect to the original URL
- Custom aliases (`/launch` instead of a random code)
- Optional expiry (TTL) per link
- Click analytics: how many times each link was opened

**Non-functional**
- Redirects must be fast: p99 under ~50 ms (this is a redirect service, nothing else)
- Highly available — a short link in a tweet can't 500
- 100M new URLs/day, read:write ratio ~100:1
- Codes are short (6–7 chars) and never collide

**Out of scope:** link preview pages, QR codes, user accounts, link editing
after creation. Say this out loud in an interview — scoping is half the grade.

## 2. Capacity estimation

| Number | Maths |
|---|---|
| Writes | 100M/day ≈ **1,200/sec** |
| Reads | 100 × writes ≈ **120,000/sec** |
| New URLs in 10 years | 100M × 365 × 10 ≈ **365B** |
| Storage (500 bytes/row) | 365B × 500 B ≈ **180 TB** |
| Code length needed | 62^7 ≈ 3.5 trillion — 7 chars covers 10 years with room |

The read-heavy ratio (100:1) drives the whole design: **cache redirects
aggressively, write path can be slower**.

## 3. API design

```
POST /shorten            { "url": "...", "customAlias": "launch", "ttlSeconds": 86400 }
→ 201 { "code": "aB3x9Q", "shortUrl": "https://sho.rt/aB3x9Q", "expiresAt": "..." }

GET /{code}              → 302 → original URL
GET /stats/{code}        → { "code", "longUrl", "clicks", "createdAt" }
```

**301 vs 302** — use **302 (temporary)**. A 301 tells browsers and search
engines "this redirect is permanent, cache it forever", so the owner can never
change where the code points. 302 keeps control on the server. This is the
detail interviewers love; the code comments say the same.

## 4. How codes are generated

Three options, one winner:

| Approach | How | Problem |
|---|---|---|
| Hash the URL (MD5 → Base62) | Deterministic, same URL → same code | Collisions need a retry loop; a long URL's hash is still long |
| Random 7-char string | Simple | Must check the DB for a collision on every write (extra read) |
| **Counter → Base62 (used here)** | Monotonic id, encoded | Needs a unique-id generator |

The counter approach is what this repo implements: `Interlocked.Increment`
gives the next id, `Base62.Encode` turns it into a short code. Same URL
shortened twice returns the same code (deduplication), and two different URLs
can never collide.

**At scale the counter becomes a distributed id generator:**
- **Twitter Snowflake style:** 64-bit id = timestamp + machine id + sequence.
  No coordination, roughly time-ordered, ~4k ids/sec per machine.
- **A dedicated id service:** one service hands out id ranges (1–1M to server A,
  1M–2M to server B…). Simple, but a single point of failure — keep a standby.
- **ZooKeeper/etcd counter:** correct, but a network round-trip per id; only
  worth it at modest scale.

## 5. Data model

```sql
urls (
  id          BIGINT PRIMARY KEY,   -- the counter value
  code        VARCHAR(7) UNIQUE,    -- Base62(id)
  long_url    TEXT NOT NULL,
  created_at  TIMESTAMP NOT NULL,
  expires_at  TIMESTAMP NULL
)

clicks (append-only, for analytics)
  code, clicked_at, country, referrer, user_agent
```

- Index on `code` (every read is a lookup by code).
- Click analytics go to a **separate append-only store** — never let analytics
  writes slow down the redirect path. Aggregate offline (daily rollups).
- Shard by `code` hash when one database stops being enough; 180 TB over
  10 years means sharding is a "year 3" problem, not a day-1 problem.

## 6. Scaling the read path

```
User → Load balancer → App servers → Cache (Redis) → Database
                              ↓ (miss)
                         Database (read replicas)
```

1. **Cache first:** 120k reads/sec is trivial for Redis; ~20% of links get
   80% of the traffic, so cache hit rates are high.
2. **Read replicas** for cache misses.
3. **CDN in front** for the hottest links (a viral tweet's link can be served
   from the edge without touching your servers at all).
4. Writes stay on the primary — 1,200/sec is nothing for a single Postgres/MySQL.

## 7. Failure modes

- **Cache down:** fall back to the database; slower, still correct.
- **Database down:** reads degrade (serve from cache with stale TTLs); writes
  queue and retry. Never accept a shorten you can't persist.
- **Id service down:** the write path stalls. Mitigation: each app server
  pre-fetches an id range so it can survive a short outage.
- **Expired links:** return 404, and lazily delete expired rows in a nightly job.

## 8. Interview talking points

- "Why not hash the URL?" — collisions + no control over code length.
- "Why 302, not 301?" — caching semantics; the owner must stay in control.
- "Where does it break first?" — the read path, at ~100k rps; that's why the
  cache sits in front of everything.
- "How do you handle a malicious user shortening a million URLs?" — per-user
  rate limiting (see [Design 2](02-rate-limiter.md)) and a blocklist for
  phishing/malware destinations.
