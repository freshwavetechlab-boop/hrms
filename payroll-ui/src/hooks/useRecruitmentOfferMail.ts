import { useCallback, useEffect, useState } from 'react'
import { getOfferMailJobs, validOfferMailJobs, type OfferMailJob } from '../services/recruitmentOfferMailService'

export function useRecruitmentOfferMail(clientId: number, enabled: boolean, scope = '') {
  const key = `${scope}:${clientId}`
  const [snapshot, setSnapshot] = useState<{ key: string; jobs: OfferMailJob[]; error: string }>({ key: '', jobs: [], error: '' })
  const [revision, setRevision] = useState(0), [loading, setLoading] = useState(false)
  const refresh = useCallback(() => setRevision(value => value + 1), [])
  useEffect(() => {
    if (!clientId || !enabled) return
    let active = true, timer: ReturnType<typeof setTimeout> | undefined
    const load = async () => {
      setLoading(true)
      const result = await getOfferMailJobs(clientId)
      if (!active) return
      const valid = result.ok && validOfferMailJobs(result.data.items) && result.data.items.every(job => job.clientId === clientId)
      setSnapshot(previous => valid ? { key, jobs: result.data.items, error: '' } : { key, jobs: result.status !== 403 && previous.key === key ? previous.jobs : [], error: result.error || 'Unable to refresh email progress. The background queue is unaffected.' })
      setLoading(false)
      if (!valid || result.data.items.some(job => !job.isComplete)) timer = setTimeout(() => void load(), 10000)
    }
    void load()
    const tick = () => { if (!document.hidden) refresh() }
    window.addEventListener('focus', tick); window.addEventListener('hrms:actions-changed', tick)
    return () => { active = false; if (timer) clearTimeout(timer); window.removeEventListener('focus', tick); window.removeEventListener('hrms:actions-changed', tick) }
  }, [clientId, enabled, key, revision, refresh])
  return { jobs: enabled && snapshot.key === key ? snapshot.jobs : [], error: enabled && snapshot.key === key ? snapshot.error : '', loading, refresh }
}
