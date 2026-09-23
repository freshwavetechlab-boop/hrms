import { createContext, useContext, useEffect, useState, type Dispatch, type SetStateAction } from 'react'
import type { RecruitmentPipelineDisplayMode } from '../types/recruitmentPipelineView'

export type RecruitmentView = 'Cards' | 'Table'
export const RecruitmentViewContext = createContext<{ view: RecruitmentView; scope: string; pipelineDisplay?: RecruitmentPipelineDisplayMode } | null>(null)
export const useRecruitmentView = () => useContext(RecruitmentViewContext)?.view
export const useRecruitmentPipelineDisplay = () => useContext(RecruitmentViewContext)?.pipelineDisplay

function readPreference<T>(key: string, fallback: T): T {
  try {
    const value = JSON.parse(sessionStorage.getItem(key) ?? 'null')
    return value !== null && typeof value === typeof fallback && Array.isArray(value) === Array.isArray(fallback) ? value : fallback
  } catch { return fallback }
}

export function useSessionPreference<T>(key: string, fallback: T): [T, Dispatch<SetStateAction<T>>] {
  const [entry, setEntry] = useState(() => ({ key, value: readPreference(key, fallback) }))
  if (entry.key !== key) setEntry({ key, value: readPreference(key, fallback) })
  useEffect(() => {
    if (entry.key !== key) return
    try { sessionStorage.setItem(key, JSON.stringify(entry.value)) } catch { /* Storage can be unavailable. */ }
  }, [entry, key])
  return [entry.key === key ? entry.value : readPreference(key, fallback), next => setEntry(current => ({
    key,
    value: typeof next === 'function' ? (next as (value: T) => T)(current.key === key ? current.value : readPreference(key, fallback)) : next,
  }))]
}

export function useRecruitmentPreference<T>(name: string, fallback: T) {
  const context = useContext(RecruitmentViewContext)
  return useSessionPreference(`recruitment.ui:${context?.scope ?? 'local'}:${window.location.pathname}:${name}`, fallback)
}
