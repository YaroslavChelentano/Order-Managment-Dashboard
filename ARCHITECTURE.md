# Architecture

## Overview

This submission is a **procurement order-management** API and dashboard. The backend is **ASP.NET Core on .NET 10** with **PostgreSQL** (Npgsql + Dapper) and **Redis** (StackExchange.Redis) for bulk job queues. The frontend is **React 18 + TypeScript** (Vite), built into `src/api/wwwroot` and served from the same Kestrel host on **port 3000**.

## Repository layout

| Path | Purpose |
|------|---------|
| `src/api/` | .NET web app: HTTP API, background worker, schema, optional static SPA |
| `src/client/` | Vite React UI; `npm run build` outputs to `src/api/wwwroot` |
| `data/` | CSV seed files (read-only for the assignment) |
| `tests/` | Vitest HTTP contract suite (authoritative spec) |

## Database schema

Tables: `categories`, `suppliers`, `products`, `orders`. Key columns on `orders`: `status`, `priority`, `supplier_id`, `warehouse`, `created_at`, `total_price`, `product_id`, `version` (optimistic concurrency), `flagged` (bulk **flag** action).

Indexes support filters, sorts, joins, and `ILIKE` search via **`pg_trgm`** on `products.name`.

Schema and extension are applied on startup by `BootstrapHostedService` (`Sql/schema.sql`). If `orders` is empty, CSVs are imported from `../../data` relative to the API content root.

## API compatibility shims

Automated tests require multiple bulk entry points and response aliases:

- `POST /api/orders/bulk-action`, `POST /api/orders/bulk-actions`, and `POST /api/orders/bulk` enqueue the same job.
- Request bodies accept both `orderIds` and `order_ids` (parsed via `JsonDocument`).
- Responses include both `jobId` and `job_id` for job creation (`202`).

JSON uses **snake_case** for domain fields (`System.Text.Json` naming policy).

## Concurrency

- **PATCH /api/orders/:id** uses `version` in the `UPDATE … WHERE id = @id AND version = @v` clause. A second concurrent update affects zero rows and returns **409** with `{ error, code }`.
- **Bulk jobs** process each order inside a transaction after `pg_advisory_xact_lock(hashtextextended(order_id, 0))`, serializing overlapping batches so state transitions stay consistent while both jobs can report progress on their own ID lists.

## Background processing

Bulk actions **enqueue** immediately (Redis list + job metadata hash + payload string) and return under the time budget. `BulkJobWorker` **BRPOP**s the queue and updates PostgreSQL, then increments Redis hash fields `completed` / `failed` and sets `status` to `completed` or `failed`. **GET /api/jobs/:id** reads that hash.

## Real-time events

`/api/events` accepts a **plain WebSocket** (tests try WS before SSE). `EventBroadcaster` keeps a thread-safe set of sockets; **order_updated** is sent when a PATCH changes status (filtered clients use `?supplier_id=`). **bulk_completed** is broadcast to all subscribers with `{ jobId }` in the payload.

SignalR is not used because the test client speaks raw JSON text frames.

## Frontend

Feature-style pages under `src/client/src/pages` use **TanStack Query** for server state, **React Router** for `/`, `/dashboard`, `/suppliers/:id`. Vite dev server proxies `/api` to `localhost:3000`.

## Tradeoffs

- **SQL-first** stats, anomalies, and listings to satisfy performance tests and avoid loading 50k rows into memory.
- **Pragmatic layering**: domain logic lives next to Dapper in `OrderDb` rather than a heavy domain model, to ship a correct suite quickly.
- **Redis required** for bulk tests; connection string defaults to `localhost:6379`.
