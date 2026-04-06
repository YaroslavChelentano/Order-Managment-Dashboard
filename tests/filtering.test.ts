import { get } from './helpers/api.js';

interface Order {
  id: string;
  supplier_id: string;
  product_id: string;
  product_name?: string;
  quantity: number;
  unit_price: number;
  total_price: number;
  status: string;
  priority: string;
  created_at: string;
  updated_at: string;
  warehouse: string;
  notes?: string;
}

interface OrderListResponse {
  data: Order[];
  total?: number;
  page?: number;
  limit?: number;
}

describe('Filtering, sorting, and search', () => {
  it('filters orders by single status (pending)', async () => {
    const { data, ok } = await get<OrderListResponse>('/api/orders?status=pending');
    expect(ok).toBe(true);
    expect(data.data.length).toBeGreaterThan(0);
    for (const order of data.data) {
      expect(order.status).toBe('pending');
    }
  });

  it('filters orders by multiple statuses (pending,approved)', async () => {
    const { data, ok } = await get<OrderListResponse>('/api/orders?status=pending,approved');
    expect(ok).toBe(true);
    expect(data.data.length).toBeGreaterThan(0);

    const statuses = new Set(data.data.map((o) => o.status));
    for (const order of data.data) {
      expect(['pending', 'approved']).toContain(order.status);
    }
    // Both statuses should be represented in the results
    expect(statuses.has('pending')).toBe(true);
    expect(statuses.has('approved')).toBe(true);
  });

  it('filters orders by combined priority and status', async () => {
    const { data, ok } = await get<OrderListResponse>(
      '/api/orders?priority=critical&status=pending'
    );
    expect(ok).toBe(true);
    expect(data.data.length).toBeGreaterThan(0);
    for (const order of data.data) {
      expect(order.priority).toBe('critical');
      expect(order.status).toBe('pending');
    }
  });

  it('filters orders by supplier_id with expected volume', async () => {
    const { data, ok } = await get<OrderListResponse>('/api/orders?supplier_id=sup_042');
    expect(ok).toBe(true);
    // sup_042 is the top supplier with 5111 orders — total should exceed 1000
    expect(data.total ?? data.data.length).toBeGreaterThan(1000);
  });

  it('filters orders by date range', async () => {
    const { data, ok } = await get<OrderListResponse>(
      '/api/orders?date_from=2024-06-01&date_to=2024-06-30'
    );
    expect(ok).toBe(true);
    expect(data.data.length).toBeGreaterThan(0);

    const from = new Date('2024-06-01T00:00:00Z').getTime();
    const to = new Date('2024-06-30T23:59:59.999Z').getTime();

    for (const order of data.data) {
      const ts = new Date(order.created_at).getTime();
      expect(ts).toBeGreaterThanOrEqual(from);
      expect(ts).toBeLessThanOrEqual(to);
    }
  });

  it('filters orders by warehouse', async () => {
    const { data, ok } = await get<OrderListResponse>('/api/orders?warehouse=warehouse_east');
    expect(ok).toBe(true);
    expect(data.data.length).toBeGreaterThan(0);
    for (const order of data.data) {
      expect(order.warehouse).toBe('warehouse_east');
    }
  });

  it('filters orders by minimum total price', async () => {
    const { data, ok } = await get<OrderListResponse>('/api/orders?min_total=1000');
    expect(ok).toBe(true);
    expect(data.data.length).toBeGreaterThan(0);
    for (const order of data.data) {
      expect(order.total_price).toBeGreaterThanOrEqual(1000);
    }
  });

  it('searches orders by product name containing "hydraulic"', async () => {
    const { data, ok } = await get<OrderListResponse>('/api/orders?search=hydraulic');
    expect(ok).toBe(true);
    expect(data.data.length).toBeGreaterThan(0);
    for (const order of data.data) {
      expect(order.product_name?.toLowerCase()).toContain('hydraulic');
    }
  });

  it('sorts orders by total_price descending', async () => {
    const { data, ok } = await get<OrderListResponse>(
      '/api/orders?sort=total_price&order=desc'
    );
    expect(ok).toBe(true);
    expect(data.data.length).toBeGreaterThan(1);

    for (let i = 1; i < data.data.length; i++) {
      expect(data.data[i - 1].total_price).toBeGreaterThanOrEqual(data.data[i].total_price);
    }
  });

  it('combines status filter, sorting, and limit', async () => {
    const { data, ok } = await get<OrderListResponse>(
      '/api/orders?status=pending&sort=created_at&order=asc&limit=10'
    );
    expect(ok).toBe(true);
    expect(data.data.length).toBeLessThanOrEqual(10);
    expect(data.data.length).toBeGreaterThan(0);

    // All results must be pending
    for (const order of data.data) {
      expect(order.status).toBe('pending');
    }

    // Dates must be in ascending order
    for (let i = 1; i < data.data.length; i++) {
      const prev = new Date(data.data[i - 1].created_at).getTime();
      const curr = new Date(data.data[i].created_at).getTime();
      expect(prev).toBeLessThanOrEqual(curr);
    }
  });
});
