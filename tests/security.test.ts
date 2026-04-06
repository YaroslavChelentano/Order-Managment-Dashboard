import { describe, it, expect } from 'vitest';
import { get, patch, post } from './helpers/api.js';

describe('Security', () => {
  // ─── Input Validation ──────────────────────────────────────────────────────────

  describe('Input Validation', () => {
    it('PATCH with SQL injection in status returns 400', async () => {
      const listRes = await get<{ data: { id: string }[] }>('/api/orders');
      const firstOrder = listRes.data.data[0];
      expect(firstOrder).toBeDefined();

      const res = await patch(`/api/orders/${firstOrder.id}`, {
        status: "'; DROP TABLE orders; --",
      });

      expect(res.status).toBe(400);
    });

    it('GET /api/orders?limit=-1 returns 400 or 200 with sensible clamped limit', async () => {
      const res = await get<{ data: unknown[] }>('/api/orders?limit=-1');

      if (res.status === 400) {
        expect(res.status).toBe(400);
      } else {
        expect(res.status).toBe(200);
        expect(res.data.data.length).toBeLessThanOrEqual(100);
      }
    });

    it('bulk action with 10001 orderIds returns 400', async () => {
      const orderIds = Array.from({ length: 10001 }, (_, i) =>
        `ord_${String(i).padStart(5, '0')}`,
      );

      const res = await post('/api/orders/bulk', {
        action: 'approve',
        orderIds,
      });

      expect(res.status).toBe(400);
    });
  });

  // ─── XSS Prevention ───────────────────────────────────────────────────────────

  describe('XSS Prevention', () => {
    it('order notes with script tags: Content-Type is application/json and notes is a string', async () => {
      const listRes = await get<{ data: { id: string }[] }>('/api/orders');
      const firstOrder = listRes.data.data[0];
      expect(firstOrder).toBeDefined();

      const xssPayload = '<script>alert("xss")</script>';
      const res = await patch<Record<string, unknown>>(`/api/orders/${firstOrder.id}`, {
        notes: xssPayload,
      });

      const contentType = res.headers.get('content-type') || '';
      expect(contentType).toContain('application/json');

      if (res.status === 200) {
        expect(typeof res.data.notes).toBe('string');
      }
    });

    it('bulk action with XSS in reason field is accepted', async () => {
      const listRes = await get<{ data: { id: string; status: string }[] }>(
        '/api/orders?status=pending&limit=3',
      );
      const orderIds = listRes.data.data
        .filter((o) => o.status === 'pending')
        .map((o) => o.id);
      expect(orderIds.length).toBeGreaterThan(0);

      const res = await post('/api/orders/bulk', {
        action: 'approve',
        orderIds,
        reason: '<img src=x onerror=alert(1)>',
      });

      expect([200, 202]).toContain(res.status);
    });
  });
});
