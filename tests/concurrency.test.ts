import { describe, it, expect } from 'vitest';
import { get, patch, post } from './helpers/api.js';
import { pollJobUntilDone } from './helpers/poll.js';

describe('Concurrency', () => {
  // ─── Optimistic Locking ────────────────────────────────────────────────────

  describe('Optimistic Locking', () => {
    it('two simultaneous PATCH to same order — one gets 200, other gets 409', async () => {
      const listRes = await get<{ data: { id: string; status: string }[] }>(
        '/api/orders?limit=200',
      );
      const pendingOrder = listRes.data.data.find((o) => o.status === 'pending');
      expect(pendingOrder).toBeDefined();

      const orderId = pendingOrder!.id;

      const [res1, res2] = await Promise.all([
        patch(`/api/orders/${orderId}`, { status: 'approved' }),
        patch(`/api/orders/${orderId}`, { status: 'approved' }),
      ]);

      const statuses = [res1.status, res2.status].sort();
      expect(statuses).toEqual([200, 409]);
    });

    it('409 response contains an error property', async () => {
      const listRes = await get<{ data: { id: string; status: string }[] }>(
        '/api/orders?limit=200',
      );
      const pendingOrder = listRes.data.data.find((o) => o.status === 'pending');
      expect(pendingOrder).toBeDefined();

      const orderId = pendingOrder!.id;

      const [res1, res2] = await Promise.all([
        patch<{ error?: string }>(`/api/orders/${orderId}`, { status: 'approved' }),
        patch<{ error?: string }>(`/api/orders/${orderId}`, { status: 'approved' }),
      ]);

      const conflictRes = res1.status === 409 ? res1 : res2;
      expect(conflictRes.status).toBe(409);
      expect(conflictRes.data).toHaveProperty('error');
    });
  });

  // ─── Bulk Overlap ──────────────────────────────────────────────────────────

  describe('Bulk Overlap', () => {
    it('two bulk actions with 50% overlapping IDs — combined completed+failed >= unique count', async () => {
      // 20 unique IDs: batch1 = 0-14, batch2 = 10-19 (overlap: 10-14)
      const listRes = await get<{ data: { id: string }[] }>('/api/orders?limit=20');
      const allIds = listRes.data.data.map((o) => o.id);
      expect(allIds.length).toBe(20);

      const batch1 = allIds.slice(0, 15);
      const batch2 = allIds.slice(10, 20);

      const [job1Res, job2Res] = await Promise.all([
        post<{ job_id: string }>('/api/orders/bulk-actions', {
          order_ids: batch1,
          action: 'approve',
        }),
        post<{ job_id: string }>('/api/orders/bulk-actions', {
          order_ids: batch2,
          action: 'approve',
        }),
      ]);

      expect(job1Res.ok).toBe(true);
      expect(job2Res.ok).toBe(true);

      const [result1, result2] = await Promise.all([
        pollJobUntilDone(job1Res.data.job_id),
        pollJobUntilDone(job2Res.data.job_id),
      ]);

      const totalCompleted =
        result1.progress.completed +
        result1.progress.failed +
        result2.progress.completed +
        result2.progress.failed;

      // 20 unique IDs across both batches
      expect(totalCompleted).toBeGreaterThanOrEqual(20);
    });

    it('both overlapping bulk jobs complete successfully', async () => {
      const listRes = await get<{ data: { id: string }[] }>('/api/orders?limit=20&offset=100');
      const allIds = listRes.data.data.map((o) => o.id);
      expect(allIds.length).toBe(20);

      const batch1 = allIds.slice(0, 15);
      const batch2 = allIds.slice(10, 20);

      const [job1Res, job2Res] = await Promise.all([
        post<{ job_id: string }>('/api/orders/bulk-actions', {
          order_ids: batch1,
          action: 'approve',
        }),
        post<{ job_id: string }>('/api/orders/bulk-actions', {
          order_ids: batch2,
          action: 'approve',
        }),
      ]);

      const [result1, result2] = await Promise.all([
        pollJobUntilDone(job1Res.data.job_id),
        pollJobUntilDone(job2Res.data.job_id),
      ]);

      expect(result1.status).toBe('completed');
      expect(result2.status).toBe('completed');
    });

    it('job results reflect correct totals with smaller batches', async () => {
      // 10 unique IDs: batch1 = 0-6, batch2 = 3-9 (overlap: 3-6)
      const listRes = await get<{ data: { id: string }[] }>('/api/orders?limit=10&offset=200');
      const allIds = listRes.data.data.map((o) => o.id);
      expect(allIds.length).toBe(10);

      const batch1 = allIds.slice(0, 7);  // indices 0-6
      const batch2 = allIds.slice(3, 10); // indices 3-9

      const [job1Res, job2Res] = await Promise.all([
        post<{ job_id: string }>('/api/orders/bulk-actions', {
          order_ids: batch1,
          action: 'approve',
        }),
        post<{ job_id: string }>('/api/orders/bulk-actions', {
          order_ids: batch2,
          action: 'approve',
        }),
      ]);

      const [result1, result2] = await Promise.all([
        pollJobUntilDone(job1Res.data.job_id),
        pollJobUntilDone(job2Res.data.job_id),
      ]);

      // Each job should report its own batch total
      expect(result1.progress.total).toBe(7);
      expect(result2.progress.total).toBe(7);

      // Combined completed + failed across both should cover all 10 unique IDs
      const totalProcessed =
        result1.progress.completed +
        result1.progress.failed +
        result2.progress.completed +
        result2.progress.failed;
      expect(totalProcessed).toBeGreaterThanOrEqual(10);
    });
  });

  // ─── Read Consistency ──────────────────────────────────────────────────────

  describe('Read Consistency', () => {
    it('GET /api/orders/stats during bulk update returns consistent total_orders=50000', async () => {
      // Start a bulk action
      const listRes = await get<{ data: { id: string }[] }>('/api/orders?limit=50&offset=300');
      const orderIds = listRes.data.data.map((o) => o.id);

      const jobRes = await post<{ job_id: string }>('/api/orders/bulk-actions', {
        order_ids: orderIds,
        action: 'approve',
      });
      expect(jobRes.ok).toBe(true);

      // While bulk is processing, read stats
      const statsRes = await get<{ total_orders: number }>('/api/orders/stats');
      expect(statsRes.status).toBe(200);
      expect(statsRes.data.total_orders).toBe(50000);

      // Clean up: wait for job to finish
      await pollJobUntilDone(jobRes.data.job_id);
    });

    it('GET /api/orders/:id during bulk returns a valid status', async () => {
      const validStatuses = ['pending', 'approved', 'shipped', 'delivered', 'cancelled', 'rejected'];

      const listRes = await get<{ data: { id: string }[] }>('/api/orders?limit=10&offset=400');
      const orderIds = listRes.data.data.map((o) => o.id);
      const targetId = orderIds[0];

      // Start bulk action
      const jobRes = await post<{ job_id: string }>('/api/orders/bulk-actions', {
        order_ids: orderIds,
        action: 'approve',
      });
      expect(jobRes.ok).toBe(true);

      // Read individual order while bulk is processing
      const orderRes = await get<{ status: string }>(`/api/orders/${targetId}`);
      expect(orderRes.status).toBe(200);
      expect(validStatuses).toContain(orderRes.data.status);

      // Clean up
      await pollJobUntilDone(jobRes.data.job_id);
    });
  });

  // ─── Stress ────────────────────────────────────────────────────────────────

  describe('Stress', () => {
    it('10 concurrent PATCH to different orders — all succeed', async () => {
      const listRes = await get<{ data: { id: string; status: string }[] }>(
        '/api/orders?limit=100&offset=500',
      );
      const pendingOrders = listRes.data.data.filter((o) => o.status === 'pending');
      expect(pendingOrders.length).toBeGreaterThanOrEqual(10);

      const targets = pendingOrders.slice(0, 10);

      const results = await Promise.all(
        targets.map((order) =>
          patch(`/api/orders/${order.id}`, { status: 'approved' }),
        ),
      );

      for (const res of results) {
        expect(res.status).toBe(200);
      }
    });

    it('5 concurrent non-overlapping bulk actions — all complete', async () => {
      const listRes = await get<{ data: { id: string }[] }>('/api/orders?limit=25&offset=600');
      const allIds = listRes.data.data.map((o) => o.id);
      expect(allIds.length).toBe(25);

      // 5 batches of 5, no overlap
      const batches = Array.from({ length: 5 }, (_, i) =>
        allIds.slice(i * 5, (i + 1) * 5),
      );

      const jobResponses = await Promise.all(
        batches.map((batch) =>
          post<{ job_id: string }>('/api/orders/bulk-actions', {
            order_ids: batch,
            action: 'approve',
          }),
        ),
      );

      for (const jobRes of jobResponses) {
        expect(jobRes.ok).toBe(true);
      }

      const results = await Promise.all(
        jobResponses.map((jobRes) => pollJobUntilDone(jobRes.data.job_id)),
      );

      for (const result of results) {
        expect(result.status).toBe('completed');
      }
    });

    it('50 concurrent GET /api/orders — all 200, all < 500ms', async () => {
      const results = await Promise.all(
        Array.from({ length: 50 }, () => get('/api/orders')),
      );

      for (const res of results) {
        expect(res.status).toBe(200);
        expect(res.responseTime).toBeLessThan(500);
      }
    });
  });
});
