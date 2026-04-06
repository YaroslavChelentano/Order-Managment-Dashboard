import { describe, it, expect } from 'vitest';
import { get } from './helpers/api.js';
import expected from './expected-values.json' with { type: 'json' };

async function measureP95(fn: () => Promise<{ responseTime: number }>, iterations = 10): Promise<number> {
  const times: number[] = [];
  for (let i = 0; i < iterations; i++) {
    const result = await fn();
    times.push(result.responseTime);
  }
  times.sort((a, b) => a - b);
  return times[Math.floor(times.length * 0.95)];
}

describe('Performance', () => {
  // ─── Response Times ────────────────────────────────────────────────────────────

  describe('Response Times', () => {
    it('GET /api/orders (default) responds within 100ms p95', async () => {
      const p95 = await measureP95(() => get('/api/orders'));
      expect(p95).toBeLessThan(100);
    });

    it('GET /api/orders?status=pending&sort=created_at responds within 200ms p95', async () => {
      const p95 = await measureP95(() =>
        get('/api/orders?status=pending&sort=created_at'),
      );
      expect(p95).toBeLessThan(200);
    });

    it('GET /api/orders?search=hydraulic responds within 300ms p95', async () => {
      const p95 = await measureP95(() =>
        get('/api/orders?search=hydraulic'),
      );
      expect(p95).toBeLessThan(300);
    });

    it('GET /api/orders/stats responds within 500ms p95', async () => {
      const p95 = await measureP95(() => get('/api/orders/stats'));
      expect(p95).toBeLessThan(500);
    });

    it('GET /api/suppliers/sup_042/performance responds within 500ms p95', async () => {
      const p95 = await measureP95(() =>
        get('/api/suppliers/sup_042/performance'),
      );
      expect(p95).toBeLessThan(500);
    });

    it('GET /api/orders/anomalies responds within 1000ms p95', async () => {
      const p95 = await measureP95(() => get('/api/orders/anomalies'));
      expect(p95).toBeLessThan(1000);
    });
  });

  // ─── Data Completeness ─────────────────────────────────────────────────────────

  describe('Data Completeness', () => {
    it('orders total matches expected count', async () => {
      const res = await get<{ total: number }>('/api/orders');
      expect(res.data.total).toBe(expected.counts.orders);
    });

    it('suppliers total matches expected count', async () => {
      const res = await get<{ total: number }>('/api/suppliers');
      expect(res.data.total).toBe(expected.counts.suppliers);
    });
  });
});
