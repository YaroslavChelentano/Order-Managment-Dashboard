import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useCallback, useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { apiGet, apiPost } from '../api/client'
import { ORDER_PRIORITIES, ORDER_STATUSES, ORDER_WAREHOUSES } from '../constants/orderFilters'
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
  const [bulkAction, setBulkAction] = useState<'approve' | 'reject' | 'flag'>('approve')
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

  const filterKey = searchParams.toString()

  if (isLoading) return <div className="panel">Loading orders…</div>
  if (isError) return <div className="panel error">{(error as Error).message}</div>
  if (!data?.data.length)
    return (
      <div className="panel">
        <p className="muted">No orders match the current filters.</p>
        <Filters key={filterKey} sp={searchParams} onChange={updateFilter} />
      </div>
    )

  const limit = data.limit ?? 20
  const offset = data.offset ?? 0

  return (
    <div>
      <div className="panel filters">
        <Filters key={filterKey} sp={searchParams} onChange={updateFilter} />
      </div>
      <div className="panel bulk-bar">
        <span className="muted">{selected.size} selected</span>
        <label className="bulk-action-label">
          <span className="muted">Action</span>
          <select
            className="bulk-action-select"
            value={bulkAction}
            disabled={!selected.size || bulk.isPending}
            onChange={(e) => setBulkAction(e.target.value as 'approve' | 'reject' | 'flag')}
          >
            <option value="approve">Approve</option>
            <option value="reject">Reject</option>
            <option value="flag">Flag</option>
          </select>
        </label>
        <button type="button" className="primary" disabled={!selected.size || bulk.isPending} onClick={() => bulk.mutate(bulkAction)}>
          Apply
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

function parseStatusesParam(raw: string | null): string[] {
  if (!raw) return []
  return raw.split(',').map((s) => s.trim()).filter(Boolean)
}

function statusFilterLabel(selected: string[]): string {
  const known = ORDER_STATUSES.filter((s) => selected.includes(s))
  if (known.length === 0) return 'Any'
  if (known.length === 1) return known[0]
  if (known.length <= 2) return known.join(', ')
  return `${known.length} selected`
}

function Filters({ sp, onChange }: { sp: URLSearchParams; onChange: (k: string, v: string) => void }) {
  const statusSelected = parseStatusesParam(sp.get('status'))

  const toggleStatus = (code: string, checked: boolean) => {
    const set = new Set(statusSelected.filter((s) => ORDER_STATUSES.includes(s as (typeof ORDER_STATUSES)[number])))
    if (checked) set.add(code)
    else set.delete(code)
    const next = ORDER_STATUSES.filter((s) => set.has(s))
    onChange('status', next.join(','))
  }

  return (
    <>
      <div className="filter-field">
        <span>Status</span>
        <details className="filter-dropdown">
          <summary className="filter-dropdown__trigger">{statusFilterLabel(statusSelected)}</summary>
          <div className="filter-dropdown__panel" onClick={(e) => e.stopPropagation()}>
            {ORDER_STATUSES.map((s) => (
              <label key={s} className="filter-dropdown__option">
                <input
                  type="checkbox"
                  checked={statusSelected.includes(s)}
                  onChange={(e) => toggleStatus(s, e.target.checked)}
                />
                <span>{s}</span>
              </label>
            ))}
          </div>
        </details>
      </div>
      <label>
        Priority
        <select value={sp.get('priority') ?? ''} onChange={(e) => onChange('priority', e.target.value)}>
          <option value="">Any</option>
          {ORDER_PRIORITIES.map((p) => (
            <option key={p} value={p}>
              {p}
            </option>
          ))}
        </select>
      </label>
      <label>
        Supplier ID
        <input defaultValue={sp.get('supplier_id') ?? ''} onBlur={(e) => onChange('supplier_id', e.target.value.trim())} placeholder="e.g. sup_042" />
      </label>
      <label>
        Warehouse
        <select value={sp.get('warehouse') ?? ''} onChange={(e) => onChange('warehouse', e.target.value)}>
          <option value="">Any</option>
          {ORDER_WAREHOUSES.map((w) => (
            <option key={w} value={w}>
              {w}
            </option>
          ))}
        </select>
      </label>
      <label>
        Search product
        <input defaultValue={sp.get('search') ?? ''} onBlur={(e) => onChange('search', e.target.value)} />
      </label>
      <label>
        Sort
        <select value={sp.get('sort') ?? 'created_at'} onChange={(e) => onChange('sort', e.target.value)}>
          <option value="created_at">created_at</option>
          <option value="total_price">total_price</option>
          <option value="status">status</option>
        </select>
      </label>
      <label>
        Order
        <select value={sp.get('order') ?? 'desc'} onChange={(e) => onChange('order', e.target.value)}>
          <option value="desc">desc</option>
          <option value="asc">asc</option>
        </select>
      </label>
    </>
  )
}
