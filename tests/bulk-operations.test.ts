import { describe, it, expect } from 'vitest';
import { get, post } from './helpers/api.js';
import { pollJobUntilDone } from './helpers/poll.js';

async function getPendingOrderIds(count: number): Promise<string[]> {
  const res = await get<{ data: Array<{ id: string }> }>(`/api/orders?status=pending&limit=${count}`);
  return res.data.data.map((o) => o.id);
}

describe('Bulk Operations', () => {
  // ─── Basic ──────────────────────────────────────────────────────────────────

  describe('Basic', () => {
    it('POST /api/orders/bulk-action with 5 orderIds returns { jobId } with status 202', async () => {
      const orderIds = await getPendingOrderIds(5);
      expect(orderIds.length).toBeGreaterThanOrEqual(5);

      const res = await post<{ jobId: string }>('/api/orders/bulk-action', {
        orderIds: orderIds.slice(0, 5),
        action: 'approve',
      });

      expect(res.status).toBe(202);
      expect(res.data).toHaveProperty('jobId');
      expect(typeof res.data.jobId).toBe('string');

      // Clean up: poll job to completion before next test
      await pollJobUntilDone(res.data.jobId);
    });

    it('POST with 5 orders responds in < 500ms', async () => {
      const orderIds = await getPendingOrderIds(5);
      expect(orderIds.length).toBeGreaterThanOrEqual(5);

      const res = await post<{ jobId: string }>('/api/orders/bulk-action', {
        orderIds: orderIds.slice(0, 5),
        action: 'approve',
      });

      expect(res.responseTime).toBeLessThan(500);

      // Clean up: poll job to completion before next test
      await pollJobUntilDone(res.data.jobId);
    });

    it('GET /api/jobs/:jobId returns { status, progress: { total, completed, failed } } shape', async () => {
      const orderIds = await getPendingOrderIds(5);
      expect(orderIds.length).toBeGreaterThanOrEqual(5);

      const bulkRes = await post<{ jobId: string }>('/api/orders/bulk-action', {
        orderIds: orderIds.slice(0, 5),
        action: 'approve',
      });

      const jobRes = await get<{
        status: string;
        progress: { total: number; completed: number; failed: number };
      }>(`/api/jobs/${bulkRes.data.jobId}`);

      expect(jobRes.status).toBe(200);
      expect(jobRes.data).toHaveProperty('status');
      expect(jobRes.data).toHaveProperty('progress');
      expect(jobRes.data.progress).toHaveProperty('total');
      expect(jobRes.data.progress).toHaveProperty('completed');
      expect(jobRes.data.progress).toHaveProperty('failed');
      expect(typeof jobRes.data.progress.total).toBe('number');
      expect(typeof jobRes.data.progress.completed).toBe('number');
      expect(typeof jobRes.data.progress.failed).toBe('number');

      // Clean up: poll job to completion before next test
      await pollJobUntilDone(bulkRes.data.jobId);
    });

    it('After job completes, all 5 orders have updated status', async () => {
      const orderIds = await getPendingOrderIds(5);
      expect(orderIds.length).toBeGreaterThanOrEqual(5);

      const selectedIds = orderIds.slice(0, 5);
      const bulkRes = await post<{ jobId: string }>('/api/orders/bulk-action', {
        orderIds: selectedIds,
        action: 'approve',
      });

      await pollJobUntilDone(bulkRes.data.jobId);

      for (const id of selectedIds) {
        const orderRes = await get<{ status: string }>(`/api/orders/${id}`);
        expect(orderRes.data.status).toBe('approved');
      }
    });
  });

  // ─── Scale ──────────────────────────────────────────────────────────────────

  describe('Scale', () => {
    it('POST with 1000 orderIds responds in < 500ms (skip if < 100 pending)', async () => {
      const orderIds = await getPendingOrderIds(1000);
      if (orderIds.length < 100) {
        return; // skip: not enough pending orders
      }

      const res = await post<{ jobId: string }>('/api/orders/bulk-action', {
        orderIds,
        action: 'approve',
      });

      expect(res.responseTime).toBeLessThan(500);

      // Clean up: poll job to completion before next test
      await pollJobUntilDone(res.data.jobId, 60_000);
    });

    it('1000-order job completes within 30 seconds', async () => {
      const orderIds = await getPendingOrderIds(1000);
      if (orderIds.length < 100) {
        return; // skip: not enough pending orders
      }

      const bulkRes = await post<{ jobId: string }>('/api/orders/bulk-action', {
        orderIds,
        action: 'approve',
      });

      const job = await pollJobUntilDone(bulkRes.data.jobId, 30_000);

      expect(job.status).toBe('completed');
      expect(job.progress.total).toBe(orderIds.length);
    });

    it('API stays responsive (GET /api/orders < 200ms) during bulk job', async () => {
      const orderIds = await getPendingOrderIds(1000);
      if (orderIds.length < 100) {
        return; // skip: not enough pending orders
      }

      const bulkRes = await post<{ jobId: string }>('/api/orders/bulk-action', {
        orderIds,
        action: 'approve',
      });

      // While the job is running, check that the API is still responsive
      const healthRes = await get('/api/orders?limit=1');
      expect(healthRes.responseTime).toBeLessThan(200);

      // Clean up: poll job to completion before next test
      await pollJobUntilDone(bulkRes.data.jobId, 60_000);
    });
  });

  // ─── Error Handling ─────────────────────────────────────────────────────────

  describe('Error Handling', () => {
    it('Mix of valid + invalid IDs: valid succeed, invalid reported as failed', async () => {
      const validIds = await getPendingOrderIds(3);
      expect(validIds.length).toBeGreaterThanOrEqual(3);

      const invalidIds = ['ord_nonexistent_00001', 'ord_nonexistent_00002'];
      const mixedIds = [...validIds.slice(0, 3), ...invalidIds];

      const bulkRes = await post<{ jobId: string }>('/api/orders/bulk-action', {
        orderIds: mixedIds,
        action: 'approve',
      });

      expect(bulkRes.status).toBe(202);

      const job = await pollJobUntilDone(bulkRes.data.jobId);

      expect(job.status).toBe('completed');
      expect(job.progress.total).toBe(mixedIds.length);
      expect(job.progress.completed).toBeGreaterThanOrEqual(3);
      expect(job.progress.failed).toBeGreaterThanOrEqual(2);
    });

    it('Already-cancelled orders are skipped/failed', async () => {
      // Find cancelled orders
      const listRes = await get<{ data: Array<{ id: string; status: string }> }>(
        '/api/orders?status=cancelled&limit=3',
      );
      const cancelledIds = listRes.data.data.map((o) => o.id);
      expect(cancelledIds.length).toBeGreaterThanOrEqual(1);

      const bulkRes = await post<{ jobId: string }>('/api/orders/bulk-action', {
        orderIds: cancelledIds,
        action: 'approve',
      });

      expect(bulkRes.status).toBe(202);

      const job = await pollJobUntilDone(bulkRes.data.jobId);

      expect(job.progress.failed).toBe(cancelledIds.length);
    });

    it('Empty orderIds or invalid action returns 400', async () => {
      const emptyRes = await post('/api/orders/bulk-action', {
        orderIds: [],
        action: 'approve',
      });

      expect(emptyRes.status).toBe(400);

      const invalidActionRes = await post('/api/orders/bulk-action', {
        orderIds: ['ord_001'],
        action: 'totally_invalid_action',
      });

      expect(invalidActionRes.status).toBe(400);
    });
  });
});
