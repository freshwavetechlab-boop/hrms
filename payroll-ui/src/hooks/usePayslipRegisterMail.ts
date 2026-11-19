import { useCallback, useEffect, useState } from 'react'
import { getRegisterPayslipMail, type PayslipMailOverview } from '../services/payslipService'
import { validRegisterMail } from '../utils/payslipRegisterMail'

export function usePayslipRegisterMail(clientId: number, runId: number) {
  const key = `${clientId}:${runId}`
  const [snapshot, setSnapshot] = useState<{ key: string; data: PayslipMailOverview | null; error: string }>({ key: '', data: null, error: '' })
  const [revision, setRevision] = useState(0)
  const refresh = useCallback(() => setRevision(value => value + 1), [])
  useEffect(() => {
    if (!clientId || !runId) return
    let active = true
    let timer: ReturnType<typeof setTimeout> | undefined
    const load = async () => {
      const result = await getRegisterPayslipMail(runId)
      if (!active) return
      if (result.ok && validRegisterMail(result.data, clientId, runId)) setSnapshot({ key, data: result.data, error: '' })
      else setSnapshot(previous => ({ key, data: previous.key === key ? previous.data : null,
        error: result.error || 'Unable to verify email status. Refresh before sending.' }))
      if (!result.ok || !validRegisterMail(result.data, clientId, runId) || result.data.jobs.some(job => !job.isComplete)
        || result.data.items.some(item => item.status === 'Queued')) timer = setTimeout(() => void load(), 10000)
    }
    void load()
    window.addEventListener('focus', refresh)
    return () => { active = false; if (timer) clearTimeout(timer); window.removeEventListener('focus', refresh) }
  }, [clientId, runId, key, revision, refresh])
  const current = snapshot.key === key ? snapshot : null
  return { data: current?.data ?? null, error: current?.error ?? '', ready: !!current?.data && !current.error,
    loading: !!clientId && !!runId && !current, refresh }
}
