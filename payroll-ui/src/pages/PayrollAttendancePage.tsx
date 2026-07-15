import { useEffect, useMemo, useState } from 'react'
import { useLocation } from 'react-router-dom'
import ManualAttendanceManager from '../components/ManualAttendanceManager'
import SearchSelect from '../components/SearchSelect'
import { useToast, type ToastType } from '../components/ToastProvider'
import { getClients } from '../services/payrollService'
import { getAttendanceGroups } from '../services/leaveAttendanceService'
import type { AttendanceGroup, Client } from '../types/payroll'

type AttendanceRouteState = { clientId?: number; period?: string; groupIds?: number[]; employeeIds?: number[] } | null

const idsFrom = (value?: string | null) => Array.from(new Set((value || '').split(',').map(item => Number(item)).filter(id => Number.isFinite(id) && id > 0)))
const idsFromState = (value: unknown) => Array.isArray(value) ? Array.from(new Set(value.map(item => Number(item)).filter(id => Number.isFinite(id) && id > 0))) : []
const validPeriod = (value?: string | null) => value && /^\d{4}-\d{2}$/.test(value) ? value : ''
const policyBatchKey = (group: AttendanceGroup) => group.policyBatchId?.trim() || `group:${group.id}`
const policyBatchName = (groups: AttendanceGroup[]) => {
  const name = groups[0]?.name || 'Attendance policy'
  return name.replace(/\s+-\s+.+$/i, '').trim() || name
}

export default function PayrollAttendancePage() {
  const location = useLocation()
  const routeState = location.state as AttendanceRouteState
  const routeContext = useMemo(() => {
    const params = new URLSearchParams(location.search)
    const stateGroupIds = idsFromState(routeState?.groupIds)
    const stateEmployeeIds = idsFromState(routeState?.employeeIds)
    return {
      clientId: Number(routeState?.clientId || params.get('clientId') || 0) || 0,
      period: validPeriod(routeState?.period || params.get('period')),
      groupIds: stateGroupIds.length ? stateGroupIds : idsFrom(params.get('groupIds')),
      employeeIds: stateEmployeeIds.length ? stateEmployeeIds : idsFrom(params.get('employeeIds'))
    }
  }, [location.search, routeState])
  const [clients, setClients] = useState<Client[]>([])
  const [groups, setGroups] = useState<AttendanceGroup[]>([])
  const [clientId, setClientId] = useState(0)
  const [reviewScope, setReviewScope] = useState('')
  const [focusedEmployeeIds, setFocusedEmployeeIds] = useState<number[]>([])
  const [groupsLoaded, setGroupsLoaded] = useState(false)
  const toast = useToast()

  useEffect(() => {
    void getClients().then((rows) => {
      const active = rows.filter(row => row.isActive)
      setClients(active)
      setClientId(current => current || (routeContext.clientId && active.some(client => client.id === routeContext.clientId) ? routeContext.clientId : active[0]?.id || 0))
    })
  }, [routeContext.clientId])

  useEffect(() => {
    if (!routeContext.clientId && !routeContext.period && !routeContext.groupIds.length && !routeContext.employeeIds.length) return
    if (routeContext.clientId) setClientId(routeContext.clientId)
    setFocusedEmployeeIds(routeContext.employeeIds)
    setReviewScope(routeContext.groupIds.length ? `groups:${routeContext.groupIds.join(',')}` : '')
  }, [location.key, routeContext.clientId, routeContext.period, routeContext.groupIds, routeContext.employeeIds])

  useEffect(() => {
    if (!clientId) return
    setGroupsLoaded(false)
    void getAttendanceGroups(clientId).then(rows => {
      const active = rows.filter(row => row.isActive)
      setGroups(active)
      setReviewScope(current => {
        const firstBatch = Array.from(active.reduce((map, group) => {
          const key = policyBatchKey(group)
          if (!map.has(key)) map.set(key, [])
          map.get(key)!.push(group)
          return map
        }, new Map<string, AttendanceGroup[]>()).entries()).find(([, rows]) => rows.length > 1)?.[0] ?? ''
        const requestedIds = current.startsWith('groups:') ? idsFrom(current.slice(7)).filter(id => active.some(group => group.id === id)) : []
        if (requestedIds.length > 1) return `groups:${requestedIds.join(',')}`
        if (requestedIds.length === 1) return `group:${requestedIds[0]}`
        if (current.startsWith('batch:') && active.some(group => policyBatchKey(group) === current.slice(6))) return current
        return active.some(group => `group:${group.id}` === current) ? current : firstBatch ? `batch:${firstBatch}` : active[0] ? `group:${active[0].id}` : ''
      })
      setGroupsLoaded(true)
    })
  }, [clientId])

  const selectedGroups = useMemo(() => {
    if (reviewScope.startsWith('batch:')) {
      const key = reviewScope.slice(6)
      return groups.filter(group => policyBatchKey(group) === key)
    }
    if (reviewScope.startsWith('groups:')) {
      const ids = idsFrom(reviewScope.slice(7))
      return groups.filter(group => ids.includes(group.id))
    }
    const group = groups.find(row => `group:${row.id}` === reviewScope)
    return group ? [group] : []
  }, [groups, reviewScope])
  const selectedGroup = useMemo(() => {
    if (!selectedGroups.length) return null
    const focusedIds = focusedEmployeeIds.filter(id => selectedGroups.some(group => group.employeeIds.includes(id)))
    if (selectedGroups.length === 1 && !focusedIds.length) return selectedGroups[0]
    const employeeIds = focusedIds.length ? focusedIds : Array.from(new Set(selectedGroups.flatMap(group => group.employeeIds)))
    const base = selectedGroups[0]
    return {
      ...base,
      id: 0,
      name: selectedGroups.length === 1 ? base.name : `${selectedGroups.length} selected policies`,
      workLocationId: selectedGroups.length === 1 ? base.workLocationId : 0,
      workLocationName: selectedGroups.length === 1 ? base.workLocationName : 'Selected locations',
      workWeek: selectedGroups.length === 1 ? base.workWeek : '',
      employeeIds,
      employeeCount: employeeIds.length,
      employeeNames: ''
    }
  }, [focusedEmployeeIds, selectedGroups])
  const reviewScopeOptions = useMemo(() => {
    const batchOptions = Array.from(groups.reduce((map, group) => {
      const key = policyBatchKey(group)
      if (!map.has(key)) map.set(key, [])
      map.get(key)!.push(group)
      return map
    }, new Map<string, AttendanceGroup[]>()).entries())
      .filter(([, rows]) => rows.length > 1)
      .map(([key, rows]) => ({ value: `batch:${key}`, label: `${policyBatchName(rows)} - ${Array.from(new Set(rows.flatMap(row => row.employeeIds))).length} employees` }))
    const options = [...batchOptions, ...groups.map(group => ({ value: `group:${group.id}`, label: `${group.name} - ${group.workLocationName || 'Location'}` }))]
    return reviewScope.startsWith('groups:') && selectedGroups.length > 1 ? [{ value: reviewScope, label: `${selectedGroups.length} selected policies` }, ...options] : options
  }, [groups, reviewScope, selectedGroups.length])

  if (!clientId) return <section className="pay-runs"><div className="card report-empty"><p>Create an active client before entering payroll attendance.</p></div></section>
  const clientControl = <>
    <label className="attendance-client-control attendance-client-field"><span>Client</span><SearchSelect value={clientId} onChange={value => { setClientId(Number(value)); setReviewScope(''); setFocusedEmployeeIds([]) }} options={clients.map(client => ({ value: client.id, label: client.name }))} /></label>
    <label className="attendance-client-control attendance-scope-field"><span>Attendance policy</span><SearchSelect value={reviewScope} onChange={value => { setReviewScope(value); setFocusedEmployeeIds([]) }} options={groups.length ? reviewScopeOptions : [{ value: '', label: 'No policy configured' }]} /></label>
  </>

  if (groupsLoaded && !selectedGroup) return <section className="pay-runs payroll-attendance-page">
    <div className="card attendance-policy-empty">{clientControl}<p>Create an attendance policy in Settings before reviewing attendance.</p></div>
  </section>

  return <section className="pay-runs payroll-attendance-page">
    <ManualAttendanceManager clientId={clientId} group={selectedGroup} reviewMonth={routeContext.period} clientControl={clientControl} onMessage={(message, type: ToastType = 'success') => toast(message, type)} />
  </section>
}
