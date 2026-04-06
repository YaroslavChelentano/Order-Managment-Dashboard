import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    testTimeout: 30_000,
    hookTimeout: 10_000,
    globals: true,
    sequence: {
      files: [
        'basic-crud.test.ts',
        'filtering.test.ts',
        'security.test.ts',
        'aggregations.test.ts',
        'anomalies.test.ts',
        'bulk-operations.test.ts',
        'performance.test.ts',
        'concurrency.test.ts',
        'realtime.test.ts',
      ],
    },
  },
});
