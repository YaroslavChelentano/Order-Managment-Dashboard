# Senior Fullstack Engineer — Take-Home Assignment

Build a **procurement order-management dashboard** with a REST API backend and a React frontend.

You receive a PostgreSQL database (via Docker), CSV seed data, and a comprehensive automated test suite. Your job is to import the data, implement the API, build the UI, and pass as many tests as possible.

**Time expectation:** 5-8 hours

---

## Quick Start

```bash
# 1. Start infrastructure (PostgreSQL + Redis)
docker-compose up -d

# 2. Build your application inside src/
#    Backend: Node.js (TypeScript) or .NET (C#) — your choice
#    Frontend: React + TypeScript

# 3. Import the CSV data from data/ into PostgreSQL
#    Your import script/process is part of the assignment

# 4. Start your server — it must listen on port 3000
#    http://localhost:3000

# 5. Run the test suite to check your progress
cd tests && npm install && npm test
```

---

## What You Receive

### Data Files (`data/`)

| File | Rows | Columns | Description |
|------|------|---------|-------------|
| `orders.csv` | 50,000 | `id, supplier_id, product_id, quantity, unit_price, total_price, status, priority, created_at, updated_at, warehouse, notes` | Procurement orders spanning 2 years (2023-2024). Contains intentional edge cases — see Tips. |
| `suppliers.csv` | 500 | `id, name, email, rating, country, active, created_at` | Supplier records. The `active` column is `true`/`false`. Some suppliers are inactive. Some have duplicate name variations. |
| `products.csv` | 5,000 | `id, name, category_id, sku, price` | Products linked to categories. `price` is the base/catalog price. |
| `categories.csv` | 195 | `id, name, parent_id` | Hierarchical product categories. `parent_id` references another category's `id` (or is empty for root categories). |

### Test Suite (`tests/`)

An automated test suite with **83 tests** worth **115 points**. Run it at any time to track your progress.

| File | Tests | Points | What It Validates |
|------|-------|--------|-------------------|
| `basic-crud.test.ts` | 15 | 15 | GET/PATCH orders, GET suppliers/products, 404s, error shapes |
| `filtering.test.ts` | 10 | 10 | Status/priority/supplier/warehouse/date/search/sort filters |
| `aggregations.test.ts` | 12 | 20 | Dashboard stats, monthly trends, top suppliers, supplier performance |
| `anomalies.test.ts` | 8 | 15 | Anomaly detection rules (required + bonus) |
| `bulk-operations.test.ts` | 10 | 15 | Async bulk actions, job tracking, scale, error handling |
| `concurrency.test.ts` | 10 | 15 | Optimistic locking, bulk overlap, stress tests |
| `performance.test.ts` | 8 | 10 | Response time benchmarks (p95), data completeness |
| `realtime.test.ts` | 5 | 10 | WebSocket or SSE event streaming |
| `security.test.ts` | 5 | 5 | Input validation, SQL injection prevention |

### Infrastructure

| Service | Access | Provided via |
|---------|--------|-------------|
| PostgreSQL 16 | `localhost:5432` — user: `postgres`, password: `postgres`, db: `order_ops` | `docker-compose.yml` |
| Redis 7 | `localhost:6379` | `docker-compose.yml` |

---

## Stack

| Layer | Technology |
|-------|------------|
| **Backend** | Node.js (TypeScript) **or** .NET (C#) — your choice |
| **Frontend** | React + TypeScript |
| **Database** | PostgreSQL (provided) |
| **Cache / Queuing** | Redis (provided, optional) |

Place all your source code inside `src/`. You decide the project structure.

---

## Part 1: Backend API

All endpoints live under `/api`. Your server must listen on **port 3000**.

### Response Formats

**Paginated list** (all list endpoints must use this shape):

```json
{
  "data": [ ... ],
  "total": 50000,
  "limit": 20,
  "offset": 0
}
```

**Error response** (all errors must use this shape):

```json
{
  "error": "Human-readable error message",
  "code": "ERROR_CODE"
}
```

All responses must set `Content-Type: application/json`.

---

### 1.1 Core CRUD

#### Orders

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/orders` | Paginated list. Default `limit=20`, supports `offset`. |
| `GET` | `/api/orders/:id` | Single order. Must include joined `supplier_name` and `product_name` fields. Returns `404` if not found. |
| `PATCH` | `/api/orders/:id` | Update order (e.g., `status`, `priority`). Returns `400` for invalid status, `409` if order is already `cancelled`. |

**Valid statuses:** `pending`, `approved`, `rejected`, `shipped`, `delivered`, `cancelled`

#### Suppliers

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/suppliers` | Paginated list. |
| `GET` | `/api/suppliers/:id` | Single supplier. Must include computed `order_count` and `total_revenue`. Returns `404` if not found. |

#### Products

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/products` | Paginated list. |
| `GET` | `/api/products?category=cat_001` | Filter by category. **Must include products in all child categories recursively** (not just direct children). |

---

### 1.2 Filtering, Sorting, and Search

All filters apply to `GET /api/orders` via query parameters. They can be **combined freely**.

| Parameter | Example | Behavior |
|-----------|---------|----------|
| `status` | `?status=pending` or `?status=pending,approved` | Single value or comma-separated list |
| `priority` | `?priority=critical` | Filter by priority |
| `supplier_id` | `?supplier_id=sup_042` | Filter by supplier |
| `warehouse` | `?warehouse=warehouse_east` | Filter by warehouse |
| `date_from` / `date_to` | `?date_from=2024-06-01&date_to=2024-06-30` | Filter by `created_at` range (inclusive) |
| `min_total` | `?min_total=1000` | Orders with `total_price >= 1000` |
| `search` | `?search=hydraulic` | Case-insensitive text search on joined `product_name` |
| `sort` / `order` | `?sort=total_price&order=desc` | Sort by any field. `order` is `asc` or `desc`. |
| `limit` / `offset` | `?limit=50&offset=100` | Pagination |

Example combining filters:

```
GET /api/orders?status=pending&priority=high&sort=created_at&order=asc&limit=10
```

---

### 1.3 Aggregations

#### `GET /api/orders/stats`

Returns dashboard-level aggregate metrics computed from all orders:

```json
{
  "total_orders": 50000,
  "total_revenue": 2318108880,
  "avg_order_value": 46362.18,
  "by_status": {
    "pending": { "count": 7575, "total_value": 348689800.31 },
    "approved": { "count": 9940, "total_value": 467902572.13 },
    "rejected": { "count": 4084, "total_value": 183228851.50 },
    "shipped": { "count": 5845, "total_value": 268766044.58 },
    "delivered": { "count": 17581, "total_value": 812308270.85 },
    "cancelled": { "count": 4975, "total_value": 237213340.63 }
  },
  "by_month": [
    { "month": "2023-01", "order_count": 4705, "revenue": 224662100.59 },
    { "month": "2023-02", "order_count": 1410, "revenue": 66192305.49 },
    "... (24 entries total, one per month from 2023-01 to 2024-12)"
  ],
  "top_suppliers": [
    { "supplier_id": "sup_042", "supplier_name": "...", "total_revenue": 230810064.99 },
    "... (exactly 10 entries, sorted by total_revenue descending)"
  ],
  "by_warehouse": [
    { "warehouse": "warehouse_east", "count": 9724, "total_value": 443276581.90 },
    { "warehouse": "unassigned", "count": 1512, "total_value": 69727635.30 },
    "... (6 entries: 5 warehouses + 'unassigned' for null/empty warehouse)"
  ]
}
```

**Key rules:**
- `by_month`: format is `"YYYY-MM"`, sorted chronologically. Data spans 2023-01 through 2024-12 (24 months).
- `top_suppliers`: exactly 10, sorted by `total_revenue` descending.
- `by_warehouse`: orders with `null` or empty `warehouse` must appear as `"unassigned"`.

#### `GET /api/suppliers/:id/performance`

Returns performance metrics for a specific supplier:

```json
{
  "avg_delivery_days": 9.41,
  "rejection_rate": 0.091,
  "avg_order_value": 45159.47,
  "monthly_trend": [
    { "month": "2023-01", "order_count": 480 },
    "... (one entry per month this supplier has orders)"
  ],
  "price_consistency": 0.6486
}
```

| Field | How to compute |
|-------|---------------|
| `avg_delivery_days` | For orders with `status=delivered`: average of `(updated_at - created_at)` in days |
| `rejection_rate` | Count of `status=rejected` / total orders for this supplier (0.0 to 1.0) |
| `avg_order_value` | Mean `total_price` across all of this supplier's orders |
| `monthly_trend` | Group this supplier's orders by month. One `{ month, order_count }` per month. |
| `price_consistency` | Fraction of this supplier's orders where `unit_price` is within 20% of the product's base `price` (from products.csv). Range: 0.0 to 1.0. |

---

### 1.4 Anomaly Detection

#### `GET /api/orders/anomalies`

Scan orders for data-quality issues and return flagged records:

```json
{
  "data": [
    {
      "order_id": "ord_00150",
      "anomaly_types": ["price_mismatch", "negative_quantity"],
      "severity": "high"
    }
  ]
}
```

**Required rules (must implement):**

| Rule | Condition | What to look for in the data |
|------|-----------|------------------------------|
| `price_mismatch` | `abs(total_price - quantity * unit_price) > 0.01` | ~2% of orders have intentionally wrong totals |
| `inactive_supplier` | Order's supplier has `active = false` | Some inactive suppliers still have recent orders |
| `negative_quantity` | `quantity < 0` | These represent returns |
| `timestamp_anomaly` | `updated_at < created_at` | ~200 orders have impossible timestamps |

**Bonus rules (extra credit — earn additional points):**

| Rule | Condition |
|------|-----------|
| `price_spike` | `unit_price` is significantly above the product's base `price` (e.g., > 3x) |
| `after_hours` | Order `created_at` is outside business hours (e.g., 22:00-06:00 UTC) |
| `risky_supplier` | Supplier has an unusually high rate of anomalous orders (e.g., > 50%) |

**Each anomaly must include:**
- `order_id`: the order's ID
- `anomaly_types`: array of strings identifying which rules matched (an order can match multiple)
- `severity`: one of `"low"`, `"medium"`, `"high"` — you decide the classification logic

---

### 1.5 Bulk Operations

#### `POST /api/orders/bulk-action`

Accepts a batch of order IDs and an action. Must process **asynchronously** (in the background) and return immediately.

**Request:**
```json
{
  "orderIds": ["ord_00001", "ord_00002", "ord_00003"],
  "action": "approve",
  "reason": "Batch approval for Q1"
}
```

Valid actions: `approve`, `reject`, `flag`

**Response — `202 Accepted`:**
```json
{
  "jobId": "job_abc123"
}
```

**Rules:**
- Must respond in **< 500ms** regardless of batch size (up to 10,000)
- Empty `orderIds` or invalid `action` → `400`
- Batches exceeding 10,000 IDs → `400`
- Non-existent order IDs are counted as `failed` in the job progress
- Already-cancelled orders are counted as `failed`

#### `GET /api/jobs/:id`

Poll for job status:

```json
{
  "status": "processing",
  "progress": {
    "total": 1000,
    "completed": 450,
    "failed": 3
  }
}
```

`status` values: `"processing"` → `"completed"` (or `"failed"` if everything failed)

---

### 1.6 Concurrency

Your API must handle concurrent access correctly:

| Scenario | Expected behavior |
|----------|-------------------|
| Two simultaneous `PATCH` to the same order | One succeeds (`200`), the other gets `409 Conflict` with an `error` field |
| Two bulk actions with overlapping order IDs | Both jobs complete. Each order is processed exactly once — no double-processing. |
| `GET` requests during a bulk operation | Return valid, consistent data. `total_orders` stays at 50,000. Individual orders have a valid status (old or new, never corrupt). |

---

### 1.7 Real-Time Events

Expose a real-time event stream at **`/api/events`** using **WebSocket** or **Server-Sent Events (SSE)** — your choice. The test suite auto-detects which protocol you support.

#### Events to emit:

**`order_updated`** — when any order's status changes:
```json
{
  "type": "order_updated",
  "data": {
    "id": "ord_00042",
    "old_status": "pending",
    "new_status": "approved",
    "updated_at": "2024-06-15T10:30:00Z"
  }
}
```

**`bulk_completed`** — when a bulk job finishes:
```json
{
  "type": "bulk_completed",
  "data": {
    "jobId": "job_abc123"
  }
}
```

**Filtered subscriptions:** Clients can connect with `?supplier_id=sup_042` to only receive events for that supplier's orders.

**Broadcast:** All connected clients receive the same events (unless filtered).

---

## Part 2: Frontend

Build a React + TypeScript frontend that consumes your API. Include these views:

### Orders Table
- Paginated with server-side pagination
- Filter controls: status, priority, supplier, warehouse, date range
- Sortable columns
- Text search field
- Multi-select rows + bulk action buttons

### Analytics Dashboard
- Charts from `/api/orders/stats` data
- Show at minimum: status distribution, monthly order volume trend, top suppliers by revenue

### Supplier Detail View
- Click a supplier to see details + performance metrics from `/api/suppliers/:id/performance`
- Show the supplier's order history

### Bulk Action UX
- Select orders → pick action → confirm → show progress
- Display which succeeded and which failed when complete

### States
Every view must handle:
- **Loading** — spinner or skeleton while fetching
- **Error** — user-friendly message when API fails
- **Empty** — meaningful message when no data matches filters

---

## Part 3: Documentation

Create two Markdown files in your submission:

### `ARCHITECTURE.md`
Explain: project structure, database schema, indexing strategy, how you handle concurrency, how you handle background processing, how you implement real-time events, and any tradeoffs you made.

### `ANOMALY_STRATEGY.md`
Explain: which anomaly rules you implemented, how you determine severity, what patterns you discovered in the data, and what you'd improve with more time.

---

## Running the Tests

```bash
cd tests
npm install

# Run all tests (ordered from easy to hard)
npm test

# Run a single category
npm run test:basic       # Basic CRUD (start here)
npm run test:filter      # Filtering & sorting
npm run test:agg         # Aggregations
npm run test:anomaly     # Anomaly detection
npm run test:bulk        # Bulk operations
npm run test:concurrent  # Concurrency
npm run test:perf        # Performance benchmarks
npm run test:realtime    # WebSocket / SSE
npm run test:security    # Input validation

# Override API URL if not on port 3000
API_URL=http://localhost:8080 npm test
```

---

## Scoring

### Automated Tests — 115 points

| Category | Tests | Points |
|----------|-------|--------|
| Basic CRUD | 15 | 15 |
| Filtering & Sorting | 10 | 10 |
| Aggregations | 12 | 20 |
| Anomaly Detection | 8 | 15 |
| Bulk Operations | 10 | 15 |
| Concurrency | 10 | 15 |
| Performance | 8 | 10 |
| Real-Time Events | 5 | 10 |
| Security | 5 | 5 |
| **Total** | **83** | **115** |

### Qualitative Review — 30 points

| Category | Points |
|----------|--------|
| Code quality & structure | 10 |
| Frontend UX & polish | 10 |
| ARCHITECTURE.md depth | 5 |
| ANOMALY_STRATEGY.md depth | 5 |
| **Total** | **30** |

### Grand Total: 145 points

---

## Tips

1. **Start with data import and basic CRUD.** Get `npm run test:basic` passing first, then layer on features incrementally.
2. **The dataset has intentional edge cases.** Explore the CSV files before designing your schema. You'll find: null warehouses, negative quantities, mismatched prices, inactive suppliers with orders, timestamp anomalies, duplicate supplier names, circular category hierarchies, and XSS payloads in order notes.
3. **Use AI tools.** You are encouraged to use AI assistants, copilots, and code generators. We care about the result, not whether you typed every character.
4. **Commit frequently.** We review your git history. Small, well-described commits show your thought process better than one giant commit at the end.
5. **Performance matters.** The test suite measures response times against 50,000 rows. You'll need database indexes and possibly caching for the aggregation endpoints.
6. **Read the test files.** The `tests/*.test.ts` files are the authoritative specification. When the README and a test disagree, the test is right.
7. **Don't modify the test suite.** The `tests/` and `data/` directories are read-only. Only work inside `src/`.

Good luck.
