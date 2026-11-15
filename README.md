# Clinic Booking API

ASP.NET Core 9 minimal API, plain HTML/CSS/JavaScript frontend, Neon PostgreSQL, and a Docker deployment on Render.

[Open the booking page](https://clinic-booking-api-1flu.onrender.com)

API base URL: `https://clinic-booking-api-1flu.onrender.com`

## Run

Set `DATABASE_URL` to a Neon PostgreSQL URI and `API_KEY` to a random secret. For local development copy `.env.example` to `.env`, fill in values, then run `dotnet run`. The app loads `.env` locally; Render supplies real environment variables. Never commit `.env`.

Open the root webpage and enter your API key to view and book appointments. The key is kept in browser session storage, not embedded in public source. Access uses a shared API key; individual patient accounts are outside the scope of this application.

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

The server creates tables and indexes at startup. Reset also clears idempotency and rate-limit state.

## Correctness choices

- A partial unique PostgreSQL index prevents two confirmed bookings for one slot.
- Mutations acquire a transaction-scoped PostgreSQL advisory lock. This serializes writes and makes reset, cancellation, rate checks, and retries atomic across server instances. A higher-traffic system should use finer-grained locking.
- Idempotency responses are persisted in the same transaction as the booking. Replays return the original status and body; a changed request returns 422. Exact replays are served before rate limiting.
- A database-backed sliding minute window counts successful and unsuccessful attempts per normalized email. The sixth receives 429 and Retry-After.
- Stable sequence cursors avoid pagination duplicates. Cancelled bookings remain visible.

## Test

With Node.js 20+ installed, set `TEST_BASE_URL` to your test deployment and run `node test-api.mjs`. It reads the local `.env` API key. **This resets all slots and bookings** and must only be run against a test database. It covers authorization, validation, cancellation, pagination, rate limiting, 20 simultaneous attempts for one slot, and 8 simultaneous idempotent retries. It restores sample slots when finished.

## Deploy

Create a Render Docker web service using this repo and the free plan. Set DATABASE_URL and API_KEY, health check `/health`, and disable automatic deployment after submission. The Dockerfile listens on Render's PORT. Free hosting can have cold starts after inactivity.

