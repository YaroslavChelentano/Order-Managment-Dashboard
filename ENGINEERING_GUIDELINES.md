# Engineering Guidelines

This document defines how we build and maintain this codebase. **All contributors—including AI coding assistants—must follow these rules.** The goal is production-quality software that is correct, secure, maintainable, and easy to change—not theoretical purity.

---

## 1. Core Principles

- **Readability first.** Code is read far more often than it is written. Optimize for the next reader.
- **Simplicity over cleverness.** If two solutions work, prefer the one with fewer moving parts and clearer flow.
- **Correctness over speed of delivery.** Shipping fast matters; shipping wrong data or insecure behavior does not.
- **Explicit over implicit.** Magic conventions, hidden side effects, and framework “tricks” should be rare and documented.
- **Avoid unnecessary abstractions.** Introduce a layer, interface, or pattern only when you have a concrete problem it solves today—not “in case we need it later.”
- **Single responsibility at every level.** A function does one thing; a class owns one cohesive concern; a module has one reason to change.
- **Fail fast, fail clearly.** Validate inputs early; return clear errors; do not swallow exceptions without a deliberate reason.
- **Pragmatic Clean Architecture.** Respect layer boundaries, but do not create ceremony that slows delivery without benefit.

### 1.1 Automated tests as the authoritative specification

- The **`tests/`** automated suite is the **authoritative contract** for HTTP behavior, JSON shapes, and edge cases.
- If **README examples (or any doc) conflict with tests**, implement what the **tests assert**.
- When tests require **conflicting contracts** (e.g. different paths or property names), use **small, isolated compatibility layers** (duplicate route mapping, request DTO aliases, thin adapters)—do not satisfy one test file by breaking another.
- **Optimize for correctness against the full test suite first**, then for elegance. Green tests trump “cleaner” APIs that fail assertions.
- **Do not remove or refactor compatibility code** unless **all relevant tests still pass** after the change. Treat shims as intentional until the suite no longer needs them.

### 1.2 Delivery priorities

- **Prioritize passing automated tests** over “perfect” architecture. Layers and patterns exist to deliver correct behavior, not the other way around.
- **Keep implementation simple and explicit.** Straightforward control flow beats clever indirection.
- **Avoid unnecessary abstractions**—same rule as §1: no new layers, base classes, or interfaces without a concrete payoff *now*.
- **Prefer SQL** for filtering, aggregation, and reporting when the database expresses the rule clearly; avoid heavy domain modeling (rich aggregates, excessive value-object trees) when a **query + read model** suffices. Keep domain code for invariants that truly belong in application code.
- **Backend first, frontend minimal.** Get the API and **`tests/`** green before deep UI work; the frontend should satisfy README requirements without speculative features or premature polish.

---

## 2. Backend (.NET 10) Guidelines

### 2.1 Architecture

We use a **layered Clean Architecture** style with clear dependency direction:

| Layer | Responsibility | Depends on |
|-------|----------------|------------|
| **API** | HTTP concerns: routing, binding, status codes, auth hooks | Application |
| **Application** | Use cases, orchestration, DTOs for commands/queries | Domain (+ abstractions for infrastructure) |
| **Domain** | Entities, value objects, domain rules, domain events (if needed) | Nothing infrastructure-specific |
| **Infrastructure** | Dapper/Npgsql, Redis, file I/O, external APIs | Application contracts (interfaces) where inversion helps testing |

**Rules:**

- **No business logic in controllers or minimal API endpoints.** They map HTTP ↔ application services only: parse input, call a service, map result to HTTP.
- **Application services** own use-case flow: “get order by id,” “run bulk job,” “compute stats.” They coordinate repositories and domain rules; they do not open SQL connections directly if a repository already encapsulates that access.
- **Repositories** are the default home for **data access** (SQL, parameters, mapping to domain or read models). Keep SQL readable; avoid hiding critical queries behind opaque helpers.
- **Domain layer** stays free of Npgsql, `HttpClient`, and framework types where practical. Pure rules (e.g. valid status transitions) live here—but **stay lean**: do not model aggregates and graphs when **SQL + DTOs** (see §1.2) meet the use case and tests.
- **Do not skip layers** “just this once” to save time—leaks become permanent.

### 2.2 Code Style

- **Small methods.** If you need a comment to explain what a block does, extract a named method.
- **Single responsibility per method.** One clear outcome per public method when possible.
- **Avoid deep nesting.** Use early returns and guard clauses; flatten `if` ladders.
- **Explicit naming.** Use full words (`orderRepository`, `totalRevenue`)—no `mgr`, `svc`, `tmp`, `data2`.
- **Prefer `async`/`await` end-to-end** for I/O; avoid `Task.Result` / `.GetAwaiter().GetResult()` in app code.
- **File and type organization:** one primary type per file unless a small private nested type is clearly local.

### 2.3 Data Access (Dapper + Npgsql)

- **Use Dapper** for database access unless a compelling reason exists to use something else.
- **Always parameterized SQL.** Every value that comes from the user, URL, or body must be passed as a parameter—never interpolated into the query string.
- **Never concatenate user input into SQL.** String interpolation with `$"… {id} …"` for identifiers or values from requests is forbidden.
- **Prefer SQL for filtering, sorting, aggregations, and pagination.** Do not load 50k rows into memory to filter in C# when the database can do it.
- **Document non-obvious queries** with a short comment (intent, edge case, or index expectation)—not a restatement of the SQL.
- **Use transactions** when multiple writes must succeed or fail together.
- **Map to focused types:** query DTOs or domain entities; avoid `dynamic` for production paths.

**Example (good):**

```csharp
const string sql = """
    SELECT id, status, total_price
    FROM orders
    WHERE supplier_id = @SupplierId AND status = ANY(@Statuses)
    LIMIT @Limit OFFSET @Offset
    """;
await connection.QueryAsync<OrderRow>(sql, new { SupplierId = supplierId, Statuses = statuses, Limit = limit, Offset = offset });
```

**Example (bad):**

```csharp
// NEVER: user-controlled values in the string
var sql = $"SELECT * FROM orders WHERE supplier_id = '{supplierId}'";
```

### 2.4 Error Handling

- **Consistent JSON error shape** for API failures:

```json
{
  "error": "Human-readable message for clients",
  "code": "MACHINE_READABLE_CODE"
}
```

- **Do not leak internal exceptions** to clients (stack traces, connection strings, file paths). Log details server-side; return safe messages.
- **Validate all inputs** at the boundary (FluentValidation, manual guards, or built-in validation). Reject invalid state before touching the database.
- **Use appropriate HTTP status codes:** `400` validation, `404` missing resource, `409` conflict, `422` if you distinguish semantic validation, etc.
- **Centralize mapping** from exceptions/validation failures to responses where it reduces duplication—without hiding important context from logs.

### 2.5 Concurrency

- **Optimistic concurrency** for contested resources (e.g. orders): `version` column or conditional update on `updated_at`.
- **On conflict:** return **`409 Conflict`** with the standard `{ error, code }` body—not a generic `500`.
- **Document** what the client should do on `409` (e.g. refetch and retry).

### 2.6 Performance

- **Avoid N+1 queries.** Load related data with joins, batch queries, or Dapper multi-mapping where appropriate.
- **Index for real query patterns** (filters, sorts, FK joins). Verify with `EXPLAIN` when behavior is slow.
- **Avoid loading large datasets into memory.** Stream, page, or aggregate in the database.
- **Measure before micro-optimizing.** Prefer clear code; optimize hotspots backed by profiling or load tests.

### 2.7 Heavy endpoints, jobs, and scale

- **Bulk job creation** must **return quickly** (e.g. `202` with a job id): **enqueue** work to a background worker or queue; do **not** process large batches synchronously in the HTTP request.
- **`/api/orders/stats`**, **`/api/orders/anomalies`**, and similar **aggregation or scan endpoints** must be **SQL-first**: compute in the database with appropriate queries—not by loading full tables into memory in C#.
- **List and filter endpoints** must use **database-side pagination and filtering** with **indexes** aligned to real filter/sort columns (see assignment data volume).
- **Caching** (e.g. Redis, in-memory) is **optional** and should be added only for **clearly hot paths** or when needed to meet **documented performance test thresholds**. Avoid caching complexity until measurement shows it is necessary.

---

## 3. API Design Guidelines

- **RESTful resources** under a consistent prefix (e.g. `/api/orders`, `/api/suppliers/:id`).
- **Consistent naming:** plural nouns for collections; verbs only where unavoidable (e.g. bulk actions).
- **JSON property names: `snake_case`** for API responses and request bodies by default—match consumer and test expectations.
- **Pagination (lists):**

```json
{
  "data": [],
  "total": 50000,
  "limit": 20,
  "offset": 0
}
```

- **Validate every query parameter** (types, ranges, allowed enums). Reject unknown parameters only if you have a policy; otherwise ignore safely—but never trust raw strings for SQL.
- **Idempotent and safe methods** where the HTTP method implies it (`GET` must not mutate).
- **Versioning:** only introduce `/v2` when you must break clients; prefer additive changes first.

### 3.1 Contract compatibility (tests over docs)

When the automated suite expects **more than one** external shape, implement **both**—keep the difference small and obvious:

- **Bulk actions:** support **`POST /api/orders/bulk-action`** and **`POST /api/orders/bulk-actions`** if tests require both. Delegate both entry points to **one** application use case.
- **Request bodies:** where tests use different casings or names (e.g. `orderIds` vs `order_ids`, `jobId` vs `job_id`), **accept both** via a single normalized model (e.g. optional properties, custom JSON converter, or bind-then-map at the API edge).
- **Responses:** **prefer `snake_case`**. If some tests require additional property names, you may return **both aliases** on the same object (e.g. `jobId` and `job_id`) when necessary—keep this **isolated** at serialization or a tiny response DTO, and **document** it in code comments or `ARCHITECTURE.md`.
- **Backward-compatibility shims** are **acceptable** when they are **isolated**, **minimal**, and **documented**—not spread through domain logic.

---

## 4. Frontend (React + TypeScript) Guidelines

### 4.1 Architecture

- **Feature-based structure** (e.g. `features/orders`, `features/suppliers`) with colocated components, hooks, and types when they are feature-specific.
- **Separation of concerns:**
  - **UI components:** presentational; minimal logic; props in, events out.
  - **Hooks:** compose data fetching, derived state, and side effects for a feature.
  - **API layer:** functions that call `fetch`/axios with typed request/response; no JSX here.

Shared code lives in `components/`, `hooks/`, `lib/`, or `api/` only when genuinely reused.

### 4.2 Code Practices

- **Functional components only.** No class components.
- **Hooks for state and effects**—`useState`, `useReducer`, `useEffect`, custom hooks.
- **Keep components small.** Split when a file handles layout, data loading, and five unrelated branches.
- **Strong typing.** Avoid `any`; narrow unknown data at boundaries (API responses, `JSON.parse`).
- **Accessible UI** by default: labels, focus management, semantic HTML where applicable.

### 4.3 Data Fetching

- **Use TanStack Query** for server state: queries, mutations, cache invalidation, retries.
- **Every data-dependent view** handles:
  - **Loading** — skeleton or spinner
  - **Error** — clear message and recovery (retry, go back)
  - **Empty** — meaningful empty state, not a blank screen

### 4.4 State

- **Prefer server state** (TanStack Query) over duplicated local state.
- **Avoid global state** unless multiple distant components need the same mutable state—then use a minimal solution (React context, small store), not a framework dump.
- **URL as state** for filters, pagination, and shareable views where it improves UX.

---

## 5. Security Guidelines

- **Validate all inputs** on the server—never rely on the UI for security.
- **SQL injection:** parameterized queries only (see §2.3).
- **XSS:** treat user-generated content (e.g. notes, names) as untrusted. Escape or sanitize on render in React (default text binding is safer than `dangerouslySetInnerHTML`; never inject raw HTML from users without a strict sanitizer policy).
- **Do not trust client input** for authorization. Enforce permissions on the server for every sensitive action.
- **Secrets** in environment variables or secret stores—never committed to git.
- **Dependencies:** keep packages updated; address known vulnerabilities in transitive deps when practical.

---

## 6. Testing Guidelines

- **Treat the repository `tests/` suite as the product spec** for API behavior. Favor implementation that passes the suite; when in doubt, read the test file (see also §1.1).
- **Focus on behavior and business rules**—what the system must do under real conditions.
- **Cover edge cases** that production data will hit: nulls, empty lists, conflicts, invalid transitions.
- **Avoid testing implementation details** (private methods, exact call order of mocks) unless they encode critical contracts.
- **Prefer tests that survive refactors:** test public API of application services and HTTP contracts for integration tests.
- **Name tests** so failures explain what broke (`ApproveOrder_WhenAlreadyCancelled_Returns409`).
- **Integration tests** for data access and critical SQL paths when complexity warrants it.

---

## 7. AI Usage Rules (Very Important)

AI tools are **coding assistants**, not architects or owners of tradeoffs.

**Every AI-generated change must:**

- Follow this document and existing project conventions; **when README and tests disagree, follow the tests** (§1.1, §3.1).
- Respect **§1.2 delivery priorities**: tests over architectural purity, simple explicit code, SQL-first where appropriate, backend before frontend depth.
- Be **reviewable** by a human: clear names, reasonable file size, obvious control flow.
- Match the **stack and patterns already in the repo** (ASP.NET Core layers, Dapper, React + TanStack Query).
- Include **only** what the task requires—no drive-by refactors or new dependencies without justification.

**AI must avoid:**

- **Overengineering:** generic abstractions, unused interfaces, enterprise patterns for CRUD-sized problems.
- **Unnecessary patterns:** repositories wrapping a single line of Dapper without value; “factory factories”; event buses for three events.
- **Hidden magic:** reflection-heavy behavior, global statics, conventions that are not documented in code or here.
- **Copy-paste duplication** across features—extract shared logic when it is truly the same rule.
- **Ignoring tests** or skipping error/loading/empty states on the frontend.

**Human developers** remain responsible for merges, security, and production behavior. AI output is provisional until reviewed.

---

## 8. Anti-Patterns (What to Avoid)

| Anti-pattern | Why it hurts | Instead |
|--------------|--------------|---------|
| **Fat controllers** | Untestable, duplicated rules, unclear layers | Thin endpoints → application services |
| **God services** | One class knows everything; risky changes | Split by use case or bounded area |
| **Premature abstraction** | Hard to follow indirection before you have 2–3 real cases | Duplicate once, abstract on the third clear repetition |
| **Interface explosion** | Every class behind `IThing` with one implementation | Interfaces at boundaries (infra, tests), not everywhere |
| **Copy-paste logic** | Bugs fixed in one place only | Shared domain helpers or one well-tested path |
| **Ignoring tests** | Regressions ship silently | Test rules and integrations that matter |
| **Leaking exceptions** | Security and UX failure | Centralized error mapping + logging |
| **N+1 and unbounded queries** | Latency and memory failures | SQL-side filter/page/aggregate |
| **`dangerouslySetInnerHTML` with user data** | XSS | Escape, sanitize with policy, or plain text |
| **Removing compatibility shims** without verification | Broken tests, broken clients | Run full `tests/` suite; keep shims until no longer required (§1.1) |
| **In-memory full-table scans** for stats/anomalies | Memory and latency failures | SQL-first aggregates and filters (§2.7) |
| **Architecture for its own sake** | Failing tests, slower delivery | Tests and clarity first (§1.2); refactor only when green |
| **Deep frontend before API is stable** | Rework and false confidence | Backend first, minimal UI until suite passes (§1.2) |

---

## Summary

Build **thin, explicit layers**, keep **SQL safe and the primary tool for heavy reads/aggregates**, expose a **consistent API** (with **documented compatibility** where tests demand it), ship **backend and `tests/`** before investing in UI beyond README needs, use **small React features** with **TanStack Query**, validate **everything on the server**, and prioritize **passing the automated suite** over theoretical perfection. Use AI to **speed up** implementation—not to replace judgment. When in doubt, choose the simpler design that still meets security and correctness.
