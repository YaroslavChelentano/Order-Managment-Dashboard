import { useQuery } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { apiGet } from '../api/client'
import type { OrderRow, Paginated, SupplierPerformance } from '../types'

export function SupplierPage() {
  const { id } = useParams<{ id: string }>()
  const sid = id ?? ''

  const supplierQ = useQuery({
    queryKey: ['supplier', sid],
    queryFn: () =>
      apiGet<Record<string, unknown>>(`/api/suppliers/${encodeURIComponent(sid)}`),
    enabled: !!sid,
  })

  const perfQ = useQuery({
    queryKey: ['supplier-perf', sid],
    queryFn: () => apiGet<SupplierPerformance>(`/api/suppliers/${encodeURIComponent(sid)}/performance`),
    enabled: !!sid,
  })

  const ordersQ = useQuery({
    queryKey: ['supplier-orders', sid],
    queryFn: () => apiGet<Paginated<OrderRow>>(`/api/orders?supplier_id=${encodeURIComponent(sid)}&limit=50`),
    enabled: !!sid,
  })

  if (supplierQ.isLoading) return <div className="panel">Loading…</div>
  if (supplierQ.isError) return <div className="panel error">{(supplierQ.error as Error).message}</div>

  return (
    <div>
      <p>
        <Link to="/" className="link">
          ← Orders
        </Link>
      </p>
      <div className="panel">
        <h2 style={{ marginTop: 0 }}>{String(supplierQ.data?.name ?? sid)}</h2>
        <p className="muted">
          Orders: {String(supplierQ.data?.order_count ?? '—')} · Revenue: {String(supplierQ.data?.total_revenue ?? '—')}
        </p>
      </div>
      {perfQ.isLoading && <div className="panel">Loading performance…</div>}
      {perfQ.data && (
        <div className="panel">
          <h3>Performance</h3>
          <ul className="muted" style={{ lineHeight: 1.8 }}>
            <li>Avg delivery days: {perfQ.data.avg_delivery_days?.toFixed(2)}</li>
            <li>Rejection rate: {perfQ.data.rejection_rate?.toFixed(3)}</li>
            <li>Avg order value: {perfQ.data.avg_order_value?.toFixed(2)}</li>
            <li>Price consistency: {perfQ.data.price_consistency?.toFixed(4)}</li>
            <li>Monthly trend points: {perfQ.data.monthly_trend?.length}</li>
          </ul>
        </div>
      )}
      <div className="panel">
        <h3>Recent orders</h3>
        {ordersQ.isLoading && <p className="muted">Loading…</p>}
        {ordersQ.isError && <p className="error">{(ordersQ.error as Error).message}</p>}
        {ordersQ.data?.data.length === 0 && <p className="muted">No orders</p>}
        {!!ordersQ.data?.data.length && (
          <table>
            <thead>
              <tr>
                <th>ID</th>
                <th>Status</th>
                <th>Total</th>
                <th>Created</th>
              </tr>
            </thead>
            <tbody>
              {ordersQ.data.data.map((o) => (
                <tr key={o.id}>
                  <td>{o.id}</td>
                  <td>{o.status}</td>
                  <td>{Number(o.total_price).toFixed(2)}</td>
                  <td>{o.created_at?.slice(0, 10)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  )
}
