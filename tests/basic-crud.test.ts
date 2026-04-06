import { describe, it, expect } from 'vitest';
import { get, patch } from './helpers/api.js';
import expected from './expected-values.json' with { type: 'json' };

describe('Basic CRUD Operations', () => {
  // ─── Orders ───────────────────────────────────────────────────────────────────

  describe('Orders', () => {
    it('GET /api/orders returns paginated results with default limit 20', async () => {
      const res = await get<{ data: unknown[]; total: number }>('/api/orders');

      expect(res.status).toBe(200);
      expect(Array.isArray(res.data.data)).toBe(true);
      expect(res.data.data.length).toBeLessThanOrEqual(20);
      expect(res.data.total).toBe(expected.total_orders);
    });

    it('GET /api/orders?limit=50&offset=100 returns correct page', async () => {
      const res = await get<{ data: unknown[]; total: number }>(
        '/api/orders?limit=50&offset=100',
      );

      expect(res.status).toBe(200);
      expect(res.data.data.length).toBe(50);
      expect(res.data.total).toBe(expected.total_orders);
    });

    it('GET /api/orders/:id returns full order with supplier_name and product_name', async () => {
      const listRes = await get<{ data: { id: string }[] }>('/api/orders');
      const firstOrder = listRes.data.data[0];
      expect(firstOrder).toBeDefined();

      const res = await get<Record<string, unknown>>(`/api/orders/${firstOrder.id}`);

      expect(res.status).toBe(200);
      expect(res.data).toHaveProperty('supplier_name');
      expect(res.data).toHaveProperty('product_name');
    });

    it('GET /api/orders/:id returns 404 for non-existent ID', async () => {
      const res = await get('/api/orders/ord_nonexistent_99999');

      expect(res.status).toBe(404);
    });

    it('PATCH /api/orders/:id updates status', async () => {
      const listRes = await get<{ data: { id: string; status: string }[] }>(
        '/api/orders?limit=100',
      );
      const pendingOrder = listRes.data.data.find((o) => o.status === 'pending');
      expect(pendingOrder).toBeDefined();

      const res = await patch<Record<string, unknown>>(`/api/orders/${pendingOrder!.id}`, {
        status: 'approved',
      });

      expect(res.status).toBe(200);
      expect(res.data.status).toBe('approved');
    });

    it('PATCH /api/orders/:id returns 400 for invalid status value', async () => {
      const listRes = await get<{ data: { id: string }[] }>('/api/orders');
      const firstOrder = listRes.data.data[0];

      const res = await patch(`/api/orders/${firstOrder.id}`, {
        status: 'totally_invalid_status',
      });

      expect(res.status).toBe(400);
    });

    it('PATCH /api/orders/:id returns 409 if order is already cancelled', async () => {
      const listRes = await get<{ data: { id: string; status: string }[] }>(
        '/api/orders?limit=200',
      );
      const cancelledOrder = listRes.data.data.find((o) => o.status === 'cancelled');
      expect(cancelledOrder).toBeDefined();

      const res = await patch(`/api/orders/${cancelledOrder!.id}`, {
        status: 'approved',
      });

      expect(res.status).toBe(409);
    });
  });

  // ─── Suppliers ────────────────────────────────────────────────────────────────

  describe('Suppliers', () => {
    it('GET /api/suppliers returns paginated results', async () => {
      const res = await get<{ data: unknown[]; total: number }>('/api/suppliers');

      expect(res.status).toBe(200);
      expect(Array.isArray(res.data.data)).toBe(true);
      expect(res.data.total).toBe(expected.counts.suppliers);
    });

    it('GET /api/suppliers/sup_042 returns supplier with order_count and total_revenue', async () => {
      const res = await get<Record<string, unknown>>('/api/suppliers/sup_042');

      expect(res.status).toBe(200);
      expect(res.data).toHaveProperty('order_count');
      expect(res.data).toHaveProperty('total_revenue');
    });

    it('GET /api/suppliers/:id returns 404 for non-existent ID', async () => {
      const res = await get('/api/suppliers/sup_nonexistent_99999');

      expect(res.status).toBe(404);
    });
  });

  // ─── Products ─────────────────────────────────────────────────────────────────

  describe('Products', () => {
    it('GET /api/products returns paginated results', async () => {
      const res = await get<{ data: unknown[]; total: number }>('/api/products');

      expect(res.status).toBe(200);
      expect(Array.isArray(res.data.data)).toBe(true);
      expect(res.data.total).toBe(expected.counts.products);
    });

    it('GET /api/products?category=cat_001 includes products in child categories', async () => {
      const res = await get<{ data: { category_id: string }[] }>(
        '/api/products?category=cat_001',
      );

      expect(res.status).toBe(200);
      expect(Array.isArray(res.data.data)).toBe(true);

      const categoryIds = new Set(res.data.data.map((p) => p.category_id));
      expect(categoryIds.size).toBeGreaterThan(1);
    });
  });

  // ─── General ──────────────────────────────────────────────────────────────────

  describe('General', () => {
    it('All endpoints return Content-Type application/json', async () => {
      const endpoints = ['/api/orders', '/api/suppliers', '/api/products'];

      for (const endpoint of endpoints) {
        const res = await get(endpoint);
        const contentType = res.headers.get('content-type') || '';
        expect(contentType).toContain('application/json');
      }
    });

    it('Error responses follow { error } shape with string error field', async () => {
      const res = await get<{ error: unknown }>('/api/orders/ord_nonexistent_99999');

      expect(res.status).toBe(404);
      expect(res.data).toHaveProperty('error');
      expect(typeof res.data.error).toBe('string');
    });

    it('Unknown routes return 404', async () => {
      const res = await get('/api/this-route-does-not-exist');

      expect(res.status).toBe(404);
    });
  });
});
