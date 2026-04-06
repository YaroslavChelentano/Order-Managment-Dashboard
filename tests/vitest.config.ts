import { basename } from 'node:path';
import { defineConfig } from 'vitest/config';
import { BaseSequencer } from 'vitest/node';

/** Matches `npm test` in package.json. Vitest's default BaseSequencer reorders files by cache/failures/duration. */
const FILE_ORDER = [
  'aggregations.test.ts',
  'performance.test.ts',
  'anomalies.test.ts',
  'filtering.test.ts',
  'basic-crud.test.ts',
  'security.test.ts',
  'realtime.test.ts',
  'concurrency.test.ts',
  'bulk-operations.test.ts',
] as const;

function fileRank(moduleId: string): number {
  const name = basename(moduleId);
  const i = (FILE_ORDER as readonly string[]).indexOf(name);
  return i === -1 ? 999 : i;
}

class AssignmentSequencer extends BaseSequencer {
  override async sort(files: Parameters<BaseSequencer['sort']>[0]) {
    return [...files].sort((a, b) => fileRank(a.moduleId) - fileRank(b.moduleId));
  }
}

export default defineConfig({
  test: {
    testTimeout: 30_000,
    hookTimeout: 10_000,
    globals: true,
    fileParallelism: false,
    maxWorkers: 1,
    poolOptions: { threads: { singleThread: true } },
    sequence: {
      concurrent: false,
      shuffle: false,
      sequencer: AssignmentSequencer,
    },
  },
});
