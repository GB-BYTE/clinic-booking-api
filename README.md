# Clinic Booking API

ASP.NET Core 9 minimal API, plain HTML/CSS/JavaScript frontend, Neon PostgreSQL, and a Docker deployment on Render.

## Run

Set `DATABASE_URL` to a Neon PostgreSQL URI and `API_KEY` to a random secret. For local development copy `.env.example` to `.env`, fill in values, then run `dotnet run`. The app loads `.env` locally; Render supplies real environment variables. Never commit `.env`.

Open the root webpage and enter your API key to view and book appointments. The key is kept in browser session storage, not embedded in public source. This is an assessment access model, not a production patient identity system.

## Contract

All API routes require `X-API-Key`. Errors use `{ "error": "message" }`.

| Route | Purpose |
|---|---|
| POST /reset | Delete bookings and replace slots, including past slots |
| GET /slots?provider=... | List slots in start order, optionally filter provider |
| POST /bookings | Validate and book; optional Idempotency-Key |
| GET /bookings/{id} | Retrieve booking |
| DELETE /bookings/{id} | Cancel and release slot |
| GET /bookings?email=...&limit=10&cursor=... | Paginate bookings |

The server creates tables and indexes at startup. Reset also clears idempotency and rate-limit state, providing a fresh assessment fixture.

## Correctness choices

- A partial unique PostgreSQL index prevents two confirmed bookings for one slot.
- Mutations acquire a transaction-scoped PostgreSQL advisory lock. This deliberately serializes writes for this small assessment and makes reset, cancellation, rate checks, and retries atomic across server instances. A higher-traffic system should use finer-grained locking.
- Idempotency responses are persisted in the same transaction as the booking. Replays return the original status and body; a changed request returns 422. Exact replays are served before rate limiting.
- A database-backed sliding minute window counts successful and unsuccessful attempts per normalized email. The sixth receives 429 and Retry-After.
- Stable sequence cursors avoid pagination duplicates. Cancelled bookings remain visible.

## Test

With Node.js 20+ installed, set `TEST_BASE_URL` to your test deployment and run `node test-api.mjs`. It reads the local `.env` API key. **This resets all slots and bookings** and is intended only for the assessment/test database. It tests levels 1–5, including 20 simultaneous attempts for one slot and 8 simultaneous idempotent retries, then restores demo slots.

## Deploy

Create a Render Docker web service using this repo and the free plan. Set DATABASE_URL and API_KEY, health check `/health`, and disable automatic deployment after submission. The Dockerfile listens on Render's PORT. Free hosting can have cold starts after inactivity.

## Loom outline (under 5 minutes)

1. Enter the key and show available appointments (30 seconds).
2. Book an appointment and show its confirmation and reference (45 seconds).
3. Look it up by email, cancel, and show the slot available again (45 seconds).
4. Walk through Program.cs: validation, authentication, database transaction, partial unique index, persisted retries, pagination and rate limiting (2 minutes).
5. Show the passing contract tests and deployment configuration (30 seconds).

Do not change the submitted deployment until the assessors contact you.
