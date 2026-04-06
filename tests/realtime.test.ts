import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { get, patch, post } from './helpers/api.js';
import { EventsClient } from './helpers/events-client.js';

describe('Real-time Events', () => {
  let client: EventsClient;

  beforeEach(() => {
    client = new EventsClient();
  });

  afterEach(() => {
    client.disconnect();
  });

  it('order status change triggers an order_updated event within 5s', async () => {
    await client.connect();

    // Find a pending order to update
    const listRes = await get<{ data: { id: string; status: string }[] }>(
      '/api/orders?status=pending&limit=100',
    );
    const pendingOrder = listRes.data.data.find((o) => o.status === 'pending');
    expect(pendingOrder).toBeDefined();

    // Set up event listener before making the change
    const eventPromise = client.waitForEvent('order_updated', 5000);

    // Trigger the status change
    await patch(`/api/orders/${pendingOrder!.id}`, { status: 'approved' });

    // Wait for the event
    const event = await eventPromise;
    expect(event.type).toBe('order_updated');
  });

  it('order_updated event has correct shape { id, old_status, new_status, updated_at }', async () => {
    await client.connect();

    const listRes = await get<{ data: { id: string; status: string }[] }>(
      '/api/orders?status=pending&limit=100',
    );
    const pendingOrder = listRes.data.data.find((o) => o.status === 'pending');
    expect(pendingOrder).toBeDefined();

    const eventPromise = client.waitForEvent('order_updated', 5000);
    await patch(`/api/orders/${pendingOrder!.id}`, { status: 'approved' });

    const event = await eventPromise;
    expect(event.data).toHaveProperty('id');
    expect(event.data).toHaveProperty('old_status');
    expect(event.data).toHaveProperty('new_status');
    expect(event.data).toHaveProperty('updated_at');
  });

  it('filtered subscription (supplier_id=sup_042) only receives that supplier\'s events', async () => {
    await client.connect({ supplier_id: 'sup_042' });

    // Find a pending order for supplier sup_042
    const listRes = await get<{ data: { id: string; status: string; supplier_id: string }[] }>(
      '/api/orders?status=pending&supplier_id=sup_042&limit=100',
    );
    const sup042Order = listRes.data.data.find(
      (o) => o.status === 'pending' && o.supplier_id === 'sup_042',
    );
    expect(sup042Order).toBeDefined();

    const eventPromise = client.waitForEvent('order_updated', 5000);
    await patch(`/api/orders/${sup042Order!.id}`, { status: 'approved' });

    const event = await eventPromise;
    expect(event.data.id).toBe(sup042Order!.id);
  });

  it('bulk completion triggers a bulk_completed event with jobId', async () => {
    await client.connect();

    // Get some pending orders for bulk action
    const listRes = await get<{ data: { id: string; status: string }[] }>(
      '/api/orders?status=pending&limit=5',
    );
    const orderIds = listRes.data.data
      .filter((o) => o.status === 'pending')
      .map((o) => o.id);
    expect(orderIds.length).toBeGreaterThan(0);

    const eventPromise = client.waitForEvent('bulk_completed', 5000);

    await post('/api/orders/bulk', {
      action: 'approve',
      orderIds,
    });

    const event = await eventPromise;
    expect(event.type).toBe('bulk_completed');
    expect(event.data).toHaveProperty('jobId');
  });

  it('two clients both receive the same event', async () => {
    const client2 = new EventsClient();

    try {
      await client.connect();
      await client2.connect();

      const listRes = await get<{ data: { id: string; status: string }[] }>(
        '/api/orders?status=pending&limit=100',
      );
      const pendingOrder = listRes.data.data.find((o) => o.status === 'pending');
      expect(pendingOrder).toBeDefined();

      const event1Promise = client.waitForEvent('order_updated', 5000);
      const event2Promise = client2.waitForEvent('order_updated', 5000);

      await patch(`/api/orders/${pendingOrder!.id}`, { status: 'approved' });

      const [event1, event2] = await Promise.all([event1Promise, event2Promise]);
      expect(event1.type).toBe('order_updated');
      expect(event2.type).toBe('order_updated');
      expect(event1.data.id).toBe(event2.data.id);
    } finally {
      client2.disconnect();
    }
  });
});
