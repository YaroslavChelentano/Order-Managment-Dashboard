export interface Paginated<T> {
  data: T[];
  total: number;
  limit: number;
  offset: number;
}

export interface OrderRow {
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

export interface DashboardStats {
  total_orders: number;
  total_revenue: number;
  avg_order_value: number;
  by_status: Record<string, { count: number; total_value: number }>;
  by_month: { month: string; order_count: number; revenue: number }[];
  top_suppliers: { supplier_id: string; supplier_name: string; total_revenue: number }[];
  by_warehouse: { warehouse: string; count: number; total_value: number }[];
}

export interface SupplierPerformance {
  avg_delivery_days: number;
  rejection_rate: number;
  avg_order_value: number;
  monthly_trend: { month: string; order_count: number }[];
  price_consistency: number;
}
