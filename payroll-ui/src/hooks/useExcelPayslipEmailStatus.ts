import { useEffect, useMemo, useState } from 'react'
import { getExcelPayslipDeliveryStatus, type ExcelPayslipBatch, type ExcelPayslipEmailStatus } from '../services/excelPayslipService'

// Status stays separate from the immutable salary snapshot and is checked again by the send API.
export function useExcelPayslipEmailStatus(clientId: number, batch: ExcelPayslipBatch | null) {
  const activeBatch = batch?.clientId === clientId ? batch : null
  const key = activeBatch ? `${clientId}:${activeBatch.id}` : ''
  const [snapshot, setSnapshot] = useState<{ key: string; items: ExcelPayslipEmailStatus[] }>({ key: '', items: [] })
  const [loading, setLoading] = useState(false), [error, setError] = useState(''), [revision, setRevision] = useState(0)
  const refresh = () => { setLoading(true); setRevision(value => value + 1) }

  useEffect(() => {
    if (!activeBatch) return
    let active = true
    let timer: ReturnType<typeof setTimeout> | undefined
    const load = async () => {
      const result = await getExcelPayslipDeliveryStatus(clientId, activeBatch.id)
      if (!active) return
      const items = result.data?.items
      const validItems = Array.isArray(items) && items.length === activeBatch.rows.length && items.every(item =>
        item && typeof item.rowId === 'string' && typeof item.email === 'string' && typeof item.message === 'string'
        && ['Pending', 'Queued', 'Sent', 'Failed', 'Unknown'].includes(item.status)
        && item.canSend === (item.status === 'Pending'))
      const ids = new Set(validItems ? items.map(item => item.rowId) : [])
      if (result.ok && validItems && ids.size === items.length && activeBatch.rows.every(row => ids.has(row.id))) {
        setSnapshot({ key, items }); setError('')
        if (items.some(item => item.status === 'Queued')) timer = setTimeout(() => void load(), 15000)
      } else {
        setError(result.error || 'Unable to verify every payslip email status. Refresh before sending.')
      }
      setLoading(false)
    }
    void load()
    return () => { active = false; if (timer) clearTimeout(timer) }
  }, [clientId, activeBatch, key, revision])

  const statuses = useMemo(() => new Map((snapshot.key === key ? snapshot.items : []).map(item => [item.rowId, item])), [snapshot, key])
  const ready = Boolean(key) && snapshot.key === key && !loading && !error
  return { statuses, ready, loading: Boolean(key) && (loading || snapshot.key !== key) && !error, error: key ? error : '', refresh }
}
