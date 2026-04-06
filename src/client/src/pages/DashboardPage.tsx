import { useQuery } from '@tanstack/react-query'
import { Bar, BarChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { apiGet } from '../api/client'
import type { DashboardStats } from '../types'

export function DashboardPage() {
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['stats'],
    queryFn: () => apiGet<DashboardStats>('/api/orders/stats'),
  })

  if (isLoading) return <div className="panel">Loading dashboard…</div>
  if (isError) return <div className="panel error">{(error as Error).message}</div>
  if (!data) return <div className="panel muted">No data</div>

  const statusData = Object.entries(data.by_status).map(([name, v]) => ({
    name,
    count: v.count,
    value: Math.round(v.total_value),
  }))

  const monthData = data.by_month.map((m) => ({
    month: m.month,
    orders: m.order_count,
    revenue: Math.round(m.revenue),
  }))

  const topData = data.top_suppliers.map((s) => ({
    name: s.supplier_id,
    revenue: Math.round(s.total_revenue),
  }))

  return (
    <div>
      <div className="panel">
        <p>
          <strong>{data.total_orders}</strong> orders · revenue <strong>{Math.round(data.total_revenue).toLocaleString()}</strong> · avg{' '}
          <strong>{Math.round(data.avg_order_value).toLocaleString()}</strong>
        </p>
      </div>
      <div className="panel">
        <h2 style={{ marginTop: 0 }}>By status</h2>
        <div style={{ width: '100%', height: 320 }}>
          <ResponsiveContainer>
            <BarChart data={statusData}>
              <CartesianGrid strokeDasharray="3 3" stroke="#30363d" />
              <XAxis dataKey="name" stroke="#8b949e" />
              <YAxis stroke="#8b949e" />
              <Tooltip contentStyle={{ background: '#161b22', border: '1px solid #30363d' }} />
              <Legend />
              <Bar dataKey="count" fill="#58a6ff" name="Count" />
            </BarChart>
          </ResponsiveContainer>
        </div>
      </div>
      <div className="panel">
        <h2 style={{ marginTop: 0 }}>Monthly volume</h2>
        <div style={{ width: '100%', height: 320 }}>
          <ResponsiveContainer>
            <BarChart data={monthData}>
              <CartesianGrid strokeDasharray="3 3" stroke="#30363d" />
              <XAxis dataKey="month" stroke="#8b949e" angle={-45} textAnchor="end" height={80} />
              <YAxis stroke="#8b949e" />
              <Tooltip contentStyle={{ background: '#161b22', border: '1px solid #30363d' }} />
              <Bar dataKey="orders" fill="#3fb950" name="Orders" />
            </BarChart>
          </ResponsiveContainer>
        </div>
      </div>
      <div className="panel">
        <h2 style={{ marginTop: 0 }}>Top suppliers</h2>
        <div style={{ width: '100%', height: 320 }}>
          <ResponsiveContainer>
            <BarChart data={topData} layout="vertical">
              <CartesianGrid strokeDasharray="3 3" stroke="#30363d" />
              <XAxis type="number" stroke="#8b949e" />
              <YAxis type="category" dataKey="name" width={100} stroke="#8b949e" />
              <Tooltip contentStyle={{ background: '#161b22', border: '1px solid #30363d' }} />
              <Bar dataKey="revenue" fill="#d29922" name="Revenue" />
            </BarChart>
          </ResponsiveContainer>
        </div>
      </div>
    </div>
  )
}
