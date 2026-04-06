/** Aligned with backend OrderStatuses.cs — GET /api/orders?status= accepts comma-separated values. */
export const ORDER_STATUSES = [
  'pending',
  'approved',
  'rejected',
  'shipped',
  'delivered',
  'cancelled',
] as const

/** Values present in seed data (orders.csv); matches filtering tests (e.g. critical). */
export const ORDER_PRIORITIES = ['critical', 'high', 'medium', 'low'] as const

/** Distinct warehouse codes in seed data (orders.csv). */
export const ORDER_WAREHOUSES = [
  'warehouse_central',
  'warehouse_east',
  'warehouse_north',
  'warehouse_south',
  'warehouse_west',
] as const
