# Assignment Test Suite

## Setup

```bash
cd tests
npm install
```

## Running Tests

Set the `API_URL` environment variable to override the default target (`http://localhost:3000`):

```bash
export API_URL=http://localhost:4000
```

### Run all tests

```bash
npm test
```

### Run individual test suites

```bash
npm run test:basic       # Basic CRUD operations
npm run test:filter      # Filtering and query parameters
npm run test:agg         # Aggregation endpoints
npm run test:anomaly     # Anomaly detection logic
npm run test:bulk        # Bulk operations
npm run test:concurrent  # Concurrency and race conditions
npm run test:perf        # Performance benchmarks
npm run test:realtime    # WebSocket / realtime features
npm run test:security    # Security and input validation
```

### Grade a submission

```bash
npm run grade
```

This runs all tests, writes `results.json`, and prints a formatted score report.

## Test Order (easiest to hardest)

1. **basic-crud** -- standard REST create/read/update/delete
2. **filtering** -- query parameter filtering, sorting, pagination
3. **aggregations** -- computed summaries and grouped statistics
4. **anomalies** -- anomaly detection rules and severity reasoning
5. **bulk-operations** -- batch create/update/delete
6. **concurrency** -- race conditions, optimistic locking, parallel writes
7. **performance** -- response time budgets under load
8. **realtime** -- WebSocket event streaming
9. **security** -- input sanitization, auth, injection prevention
