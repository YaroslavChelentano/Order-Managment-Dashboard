import { get } from './helpers/api.js';
import expected from './expected-values.json' with { type: 'json' };

interface Anomaly {
  order_id: string;
  anomaly_types: string[];
  severity: 'low' | 'medium' | 'high';
}

interface AnomalyResponse {
  data: Anomaly[];
}

describe('Anomaly detection', () => {
  let anomalies: Anomaly[];

  beforeAll(async () => {
    const { data, ok } = await get<AnomalyResponse>('/api/orders/anomalies');
    expect(ok).toBe(true);
    expect(data.data).toBeDefined();
    expect(Array.isArray(data.data)).toBe(true);
    anomalies = data.data;
  });

  // --- Required tests ---

  it('includes orders where total_price != qty * unit_price', () => {
    const anomalyOrderIds = new Set(anomalies.map((a) => a.order_id));
    for (const id of expected.anomalies.sample_price_mismatch_ids) {
      expect(anomalyOrderIds.has(id)).toBe(true);
    }
  });

  it('includes orders from inactive suppliers', () => {
    const anomalyOrderIds = new Set(anomalies.map((a) => a.order_id));
    for (const id of expected.anomalies.sample_inactive_supplier_ids) {
      expect(anomalyOrderIds.has(id)).toBe(true);
    }
  });

  it('includes orders with negative quantities', () => {
    const anomalyOrderIds = new Set(anomalies.map((a) => a.order_id));
    for (const id of expected.anomalies.sample_negative_qty_ids) {
      expect(anomalyOrderIds.has(id)).toBe(true);
    }
  });

  it('includes orders where updated_at < created_at (timestamp anomalies)', () => {
    const timestampAnomalies = anomalies.filter((a) =>
      a.anomaly_types.some(
        (t) =>
          t.toLowerCase().includes('timestamp') ||
          t.toLowerCase().includes('date') ||
          t.toLowerCase().includes('time_travel')
      )
    );
    const minExpected = Math.floor(expected.anomalies.timestamp_anomaly_count * 0.8);
    expect(timestampAnomalies.length).toBeGreaterThanOrEqual(minExpected);
  });

  it('each anomaly has valid structure { order_id, anomaly_types[], severity }', () => {
    const validSeverities = ['low', 'medium', 'high'];
    for (const anomaly of anomalies) {
      expect(typeof anomaly.order_id).toBe('string');
      expect(anomaly.order_id.length).toBeGreaterThan(0);
      expect(Array.isArray(anomaly.anomaly_types)).toBe(true);
      expect(anomaly.anomaly_types.length).toBeGreaterThan(0);
      for (const t of anomaly.anomaly_types) {
        expect(typeof t).toBe('string');
      }
      expect(validSeverities).toContain(anomaly.severity);
    }
  });

  // --- Bonus tests ---

  it('flags price_spike orders', () => {
    const priceSpikeAnomalies = anomalies.filter((a) =>
      a.anomaly_types.some(
        (t) =>
          t.toLowerCase().includes('price_spike') ||
          t.toLowerCase().includes('price')
      )
    );
    expect(priceSpikeAnomalies.length).toBeGreaterThan(0);
  });

  it('flags after_hours orders', () => {
    const afterHoursAnomalies = anomalies.filter((a) =>
      a.anomaly_types.some(
        (t) =>
          t.toLowerCase().includes('after_hours') ||
          t.toLowerCase().includes('off_hours') ||
          t.toLowerCase().includes('unusual_time')
      )
    );
    expect(afterHoursAnomalies.length).toBeGreaterThan(0);
  });

  it('flags risky_supplier orders', () => {
    const riskySupplierAnomalies = anomalies.filter((a) =>
      a.anomaly_types.some(
        (t) =>
          t.toLowerCase().includes('risky_supplier') ||
          t.toLowerCase().includes('high_risk_supplier')
      )
    );
    expect(riskySupplierAnomalies.length).toBeGreaterThan(0);
  });
});
