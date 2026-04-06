import { get } from './helpers/api.js';
import expected from './expected-values.json' with { type: 'json' };

describe('Dashboard Stats', () => {
  let stats: any;

  beforeAll(async () => {
    const res = await get('/api/orders/stats');
    expect(res.ok).toBe(true);
    stats = res.data;
  });

  it('returns total_orders matching expected', () => {
    expect(stats.total_orders).toBe(expected.total_orders);
  });

  it('total_revenue is close to expected', () => {
    expect(stats.total_revenue).toBeCloseTo(expected.total_revenue, 0);
  });

  it('by_status has correct counts for each status', () => {
    for (const [status, vals] of Object.entries(expected.by_status)) {
      expect(stats.by_status).toHaveProperty(status);
      expect(stats.by_status[status].count).toBe((vals as any).count);
    }
  });

  it('by_status has correct total_value per status', () => {
    for (const [status, vals] of Object.entries(expected.by_status)) {
      expect(stats.by_status[status].total_value).toBeCloseTo(
        (vals as any).total_value,
        0
      );
    }
  });

  it('by_month returns correct number of entries', () => {
    expect(stats.by_month).toHaveLength(expected.by_month.length);
  });

  it('by_month entries have { month, order_count, revenue } shape with correct types', () => {
    for (const entry of stats.by_month) {
      expect(typeof entry.month).toBe('string');
      expect(typeof entry.order_count).toBe('number');
      expect(typeof entry.revenue).toBe('number');
    }
  });

  it('top_suppliers returns exactly 10, sorted by revenue desc', () => {
    expect(stats.top_suppliers).toHaveLength(10);
    for (let i = 1; i < stats.top_suppliers.length; i++) {
      expect(stats.top_suppliers[i - 1].total_revenue).toBeGreaterThanOrEqual(
        stats.top_suppliers[i].total_revenue
      );
    }
  });

  it('by_warehouse includes "unassigned" with count > 0', () => {
    const unassigned = stats.by_warehouse.find(
      (w: any) => w.warehouse === 'unassigned'
    );
    expect(unassigned).toBeDefined();
    expect(unassigned.count).toBeGreaterThan(0);
  });
});

describe('Supplier Performance (sup_042)', () => {
  let perf: any;
  const expectedPerf = expected.supplier_042_performance;

  beforeAll(async () => {
    const res = await get('/api/suppliers/sup_042/performance');
    expect(res.ok).toBe(true);
    perf = res.data;
  });

  it('avg_delivery_days within +/-20% of expected', () => {
    expect(perf.avg_delivery_days).toBeGreaterThanOrEqual(
      expectedPerf.avg_delivery_days * 0.8
    );
    expect(perf.avg_delivery_days).toBeLessThanOrEqual(
      expectedPerf.avg_delivery_days * 1.2
    );
  });

  it('rejection_rate within +/-20% of expected', () => {
    expect(perf.rejection_rate).toBeGreaterThanOrEqual(
      expectedPerf.rejection_rate * 0.8
    );
    expect(perf.rejection_rate).toBeLessThanOrEqual(
      expectedPerf.rejection_rate * 1.2
    );
  });

  it('monthly_trend length matches expected', () => {
    expect(perf.monthly_trend).toHaveLength(expectedPerf.monthly_trend_length);
  });

  it('price_consistency within +/-20% of expected', () => {
    expect(perf.price_consistency).toBeGreaterThanOrEqual(
      expectedPerf.price_consistency * 0.8
    );
    expect(perf.price_consistency).toBeLessThanOrEqual(
      expectedPerf.price_consistency * 1.2
    );
  });
});
