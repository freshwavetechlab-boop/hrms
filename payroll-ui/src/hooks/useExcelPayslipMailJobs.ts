import { useCallback, useEffect, useState } from 'react'
import { getExcelPayslipBatchMailJobs, type ExcelPayslipMailJob } from '../services/excelPayslipService'

export function useExcelPayslipMailJobs(clientId: number, batchId: string) {
  const key = `${clientId}:${batchId}`
  const [snapshot, setSnapshot] = useState<{ key: string; items: ExcelPayslipMailJob[]; error: string }>({ key: '', items: [], error: '' })
  const [revision, setRevision] = useState(0), [refreshing, setRefreshing] = useState(false)
  const refresh = useCallback(() => { setRefreshing(true); setRevision(value => value + 1) }, [])
  useEffect(() => {
    if (!clientId || !batchId) return
    let active = true
    let timer: ReturnType<typeof setTimeout> | undefined
    const load = async () => {
      const result = await getExcelPayslipBatchMailJobs(clientId, batchId)
      if (!active) return
      const items = result.data?.items
      const valid = Array.isArray(items) && items.every(item => item && item.clientId === clientId && item.batchId === batchId
        && typeof item.id === 'string' && typeof item.isComplete === 'boolean' && Number.isFinite(item.percentComplete)
        && item.percentComplete >= 0 && item.percentComplete <= 100)
      setSnapshot(previous => result.ok && valid ? { key, items, error: '' }
        : { key, items: previous.key === key ? previous.items : [], error: result.error || 'Unable to refresh email progress. The background queue is unaffected.' })
      setRefreshing(false)
      // Recover after a temporary failure as well as while deliveries are still running.
      if (!result.ok || !valid || items.some(item => !item.isComplete)) timer = setTimeout(() => void load(), 10000)
    }
    void load()
    const onFocus = () => { if (!document.hidden) refresh() }
    window.addEventListener('focus', onFocus)
    window.addEventListener('hrms:actions-changed', refresh)
    return () => { active = false; if (timer) clearTimeout(timer); window.removeEventListener('focus', onFocus); window.removeEventListener('hrms:actions-changed', refresh) }
  }, [clientId, batchId, key, revision, refresh])
  return { jobs: snapshot.key === key ? snapshot.items : [], error: snapshot.key === key ? snapshot.error : '',
    loading: Boolean(clientId && batchId) && (refreshing || snapshot.key !== key), refresh }
}
