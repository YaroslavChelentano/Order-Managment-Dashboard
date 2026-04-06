import { get } from './api.js';

interface JobStatus {
  status: 'processing' | 'completed' | 'failed';
  progress: {
    total: number;
    completed: number;
    failed: number;
  };
  results?: unknown;
}

export async function pollJobUntilDone(
  jobId: string,
  timeoutMs: number = 30_000,
  intervalMs: number = 500
): Promise<JobStatus> {
  const start = Date.now();

  while (Date.now() - start < timeoutMs) {
    const res = await get<JobStatus>(`/api/jobs/${jobId}`);
    if (!res.ok) {
      throw new Error(`Failed to poll job ${jobId}: status ${res.status}`);
    }
    if (res.data.status === 'completed' || res.data.status === 'failed') {
      return res.data;
    }
    await new Promise((r) => setTimeout(r, intervalMs));
  }

  throw new Error(`Job ${jobId} did not complete within ${timeoutMs}ms`);
}
