import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { apiGet, apiPost } from '../api/client'
import type { OrderRow, Paginated } from '../types'

function buildQuery(sp: URLSearchParams): string {
  const q = new URLSearchParams()
  ;['status', 'priority', 'supplier_id', 'warehouse', 'date_from', 'date_to', 'min_total', 'search', 'sort', 'order', 'limit', 'offset'].forEach((k) => {
    const v = sp.get(k)
    if (v) q.set(k, v)
  })
  if (!q.has('limit')) q.set('limit', '20')
  if (!q.has('offset')) q.set('offset', '0')
  return q.toString() ? `?${q}` : ''
}

async function pollJob(jobId: string): Promise<void> {
  const max = 60_000
  const start = Date.now()
  while (Date.now() - start < max) {
    const j = await apiGet<{ status: string; progress: { total: number; completed: number; failed: number } }>(
      `/api/jobs/${jobId}`,
    )
    if (j.status === 'completed' || j.status === 'failed') return
    await new Promise((r) => setTimeout(r, 400))
  }
}

export function OrdersPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const queryString = useMemo(() => buildQuery(searchParams), [searchParams])
  const qc = useQueryClient()

  const { data, isLoading, isError, error } = useQuery({
    queryKey: ['orders', queryString],
    queryFn: () => apiGet<Paginated<OrderRow>>(`/api/orders${queryString}`),
  })

  const [selected, setSelected] = useState<Set<string>>(new Set())
  const toggle = (id: string) => {
    setSelected((s) => {
      const n = new Set(s)
      if (n.has(id)) n.delete(id)
      else n.add(id)
      return n
    })
  }

  const [bulkMsg, setBulkMsg] = useState<string | null>(null)
  const bulk = useMutation({
    mutationFn: async (action: 'approve' | 'reject' | 'flag') => {
      const orderIds = [...selected]
      if (orderIds.length === 0) return
      const { status, data: body } = await apiPost<{ jobId: string }>('/api/orders/bulk-action', { orderIds, action })
      if (status !== 202 || !body.jobId) throw new Error('Bulk failed')
      await pollJob(body.jobId)
    },
    onSuccess: () => {
      setBulkMsg('Bulk job finished.')
      setSelected(new Set())
      void qc.invalidateQueries({ queryKey: ['orders'] })
    },
    onError: (e: Error) => setBulkMsg(e.message),
  })

  const updateFilter = useCallback(
    (key: string, value: string) => {
      setSearchParams((prev) => {
        const n = new URLSearchParams(prev)
        if (value) n.set(key, value)
        else n.delete(key)
        n.set('offset', '0')
        return n
      })
    },
    [setSearchParams],
  )

  if (isLoading) return <div className="panel">Loading orders…</div>
  if (isError) return <div className="panel error">{(error as Error).message}</div>
  if (!data?.data.length)
    return (
      <div className="panel">
        <p className="muted">No orders match the current filters.</p>
        <Filters sp={searchParams} onChange={updateFilter} />
      </div>
    )

  const limit = data.limit ?? 20
  const offset = data.offset ?? 0

  return (
    <div>
      <div className="panel filters">
        <Filters sp={searchParams} onChange={updateFilter} />
      </div>
      <div className="panel bulk-bar">
        <span className="muted">{selected.size} selected</span>
        <button type="button" className="primary" disabled={!selected.size || bulk.isPending} onClick={() => bulk.mutate('approve')}>
          Approve
        </button>
        <button type="button" disabled={!selected.size || bulk.isPending} onClick={() => bulk.mutate('reject')}>
          Reject
        </button>
        <button type="button" disabled={!selected.size || bulk.isPending} onClick={() => bulk.mutate('flag')}>
          Flag
        </button>
        {bulk.isPending && <span className="muted">Processing bulk job…</span>}
        {bulkMsg && <span className="muted">{bulkMsg}</span>}
      </div>
      <div className="panel" style={{ overflowX: 'auto' }}>
        <table>
          <thead>
            <tr>
              <th />
              <th>ID</th>
              <th>Supplier</th>
              <th>Product</th>
              <th>Status</th>
              <th>Priority</th>
              <th>Total</th>
              <th>Created</th>
            </tr>
          </thead>
          <tbody>
            {data.data.map((o) => (
              <tr key={o.id}>
                <td>
                  <input type="checkbox" checked={selected.has(o.id)} onChange={() => toggle(o.id)} />
                </td>
                <td>{o.id}</td>
                <td>
                  <Link className="link" to={`/suppliers/${o.supplier_id}`}>
                    {o.supplier_id}
                  </Link>
                </td>
                <td>{o.product_name ?? o.product_id}</td>
                <td>{o.status}</td>
                <td>{o.priority}</td>
                <td>{Number(o.total_price).toFixed(2)}</td>
                <td>{o.created_at?.slice(0, 10)}</td>
              </tr>
            ))}
          </tbody>
        </table>
        <div className="pagination">
          <button
            type="button"
            disabled={offset === 0}
            onClick={() => {
              setSearchParams((p) => {
                const n = new URLSearchParams(p)
                n.set('offset', String(Math.max(0, offset - limit)))
                return n
              })
            }}
          >
            Previous
          </button>
          <span className="muted">
            {offset + 1}–{offset + data.data.length} of {data.total}
          </span>
          <button
            type="button"
            disabled={offset + data.data.length >= data.total}
            onClick={() => {
              setSearchParams((p) => {
                const n = new URLSearchParams(p)
                n.set('offset', String(offset + limit))
                return n
              })
            }}
          >
            Next
          </button>
        </div>
      </div>
    </div>
  )
}

function Filters({ sp, onChange }: { sp: URLSearchParams; onChange: (k: string, v: string) => void }) {
  return (
    <>
      <label>
        Status
        <input defaultValue={sp.get('status') ?? ''} onBlur={(e) => onChange('status', e.target.value)} placeholder="pending" />
      </label>
      <label>
        Priority
        <input defaultValue={sp.get('priority') ?? ''} onBlur={(e) => onChange('priority', e.target.value)} />
      </label>
      <label>
        Supplier
        <input defaultValue={sp.get('supplier_id') ?? ''} onBlur={(e) => onChange('supplier_id', e.target.value)} />
      </label>
      <label>
        Warehouse
        <input defaultValue={sp.get('warehouse') ?? ''} onBlur={(e) => onChange('warehouse', e.target.value)} />
      </label>
      <label>
        Search product
        <input defaultValue={sp.get('search') ?? ''} onBlur={(e) => onChange('search', e.target.value)} />
      </label>
      <label>
        Sort
        <select defaultValue={sp.get('sort') ?? 'created_at'} onChange={(e) => onChange('sort', e.target.value)}>
          <option value="created_at">created_at</option>
          <option value="total_price">total_price</option>
          <option value="status">status</option>
        </select>
      </label>
      <label>
        Order
        <select defaultValue={sp.get('order') ?? 'desc'} onChange={(e) => onChange('order', e.target.value)}>
          <option value="desc">desc</option>
          <option value="asc">asc</option>
        </select>
      </label>
    </>
  )
}
