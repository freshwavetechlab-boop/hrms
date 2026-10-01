import { useEffect, useMemo, useState } from 'react'
import { Alert, Button, Segmented } from 'antd'
import { useSearchParams } from 'react-router-dom'
import { apiCatalog, type ApiCatalogRow } from '../data/apiCatalog'
import SearchSelect from './SearchSelect'
import RecruitmentRecordList from './RecruitmentRecordList'
import AttendanceDevices from './AttendanceDevices'
import ApiExplorer from './ApiExplorer'
import { PageHeaderPortal } from './layout/AppPageHeader'
import { useAuthSession } from './AuthGate'
import { getClients } from '../services/payrollService'
import { getApiDocument, type ApiDocument } from '../services/apiDocumentationService'
import { RecruitmentViewContext, useSessionPreference, type RecruitmentView } from '../hooks/useRecruitmentPreferences'
import type { Client } from '../types/payroll'

export default function ApiCatalog() {
  const session = useAuthSession()
  const [selected, setSelected] = useState<ApiCatalogRow | null>(null)
  const [document, setDocument] = useState<ApiDocument | null>(null)
  const [error, setError] = useState('')
  const [reload, setReload] = useState(0)
  const [clients, setClients] = useState<Client[]>([])
  const [params, setParams] = useSearchParams()
  const scope = `catalog:${session?.user.id}`
  const [clientId, setClientId] = useSessionPreference(scope + ':client', Number(session?.user.clientId || 0))
  const [view, setView] = useSessionPreference<RecruitmentView>(scope + ':view', 'Table')
  const [keysOpen, setKeysOpen] = useState(false)
  const canManageKeys = session?.user.permissions.some(p => ['settings.manage', 'client.settings.manage'].includes(p))
  const scoped = Boolean(session?.user.clientId && !session.user.permissions.includes('settings.manage'))
  useEffect(() => { let active = true; void getClients().then(data => { if (!active) return; const rows = data.filter(c => c.isActive); setClients(rows); setClientId(id => scoped ? Number(session?.user.clientId) : rows.some(c => c.id === id) ? id : rows[0]?.id || 0) }); return () => { active = false } }, [scope, scoped])
  useEffect(() => {
    const id = Number(params.get('clientId'))
    if (clients.some(c => c.id === id) && (!scoped || id === session?.user.clientId)) { setClientId(id); if (params.get('keys') === '1' && canManageKeys) setKeysOpen(true) }
  }, [params, clients, scoped, canManageKeys])
  useEffect(() => { let active = true; setError(''); void getApiDocument().then(result => { if (active) { setDocument(result.data); setError(result.error || (!result.data ? 'Unable to load API documentation.' : '')) } }); return () => { active = false } }, [reload])
  const rows = useMemo(() => Object.entries(document?.paths || {}).flatMap(([path, methods]) => Object.entries(methods).filter(([method]) => ['get', 'post', 'put', 'patch', 'delete', 'options', 'head'].includes(method)).map(([method, operation]): ApiCatalogRow => {
    const known = apiCatalog.find(row => row.path === path && row.method === method.toUpperCase())
    return { id: method + ' ' + path, module: known?.module || operation.tags?.[0] || path.split('/')[2] || 'General', method: method.toUpperCase(), path, purpose: known?.purpose || operation.summary || operation.operationId || method.toUpperCase() + ' ' + path, workflowCandidate: known?.workflowCandidate || 'No' }
  })), [document])
  const closeKeys = () => { setKeysOpen(false); if (params.has('keys')) { const next = new URLSearchParams(params); next.delete('keys'); setParams(next, { replace: true }) } }

  return <RecruitmentViewContext.Provider value={{ view, scope: scope + ':' + clientId, namespace: 'workflows.ui' }}>
    {!scoped && <PageHeaderPortal slot="workflows-client-controls"><SearchSelect testId="api-catalog-client" value={clientId} onChange={value => { setClientId(Number(value)); const next = new URLSearchParams(params); next.set('clientId', String(value)); setParams(next, { replace: true }) }} options={clients.map(client => ({ value: client.id, label: client.name }))} /></PageHeaderPortal>}
    <PageHeaderPortal slot="workflows-view-controls"><Segmented options={[{ label: 'Card view', value: 'Cards' }, { label: 'Table view', value: 'Table' }]} value={view} onChange={value => setView(value as RecruitmentView)} /></PageHeaderPortal>
    <PageHeaderPortal slot="workflows-page-controls">{canManageKeys && <Button type="primary" disabled={!clientId} onClick={() => setKeysOpen(true)}>API keys</Button>}</PageHeaderPortal>
    <section className="recruitment-monitor-page recruitment-experience recruitment-viewport">
      {error && <Alert type="error" showIcon message={error} action={<Button onClick={() => setReload(value => value + 1)}>Retry</Button>} />}
      <RecruitmentRecordList rows={rows.map((row, id) => ({ ...row, source: row, id }))} title={row => row.purpose} subtitle={row => `${row.method} ${row.path}`} filters={[{ key: 'module', label: 'Module', value: row => row.module }, { key: 'method', label: 'Method', value: row => row.method }]} hiddenCardColumns={['purpose', 'path']} searchPlaceholder="Search API, module or purpose" emptyText={document ? 'No API routes found.' : 'Loading API documentation…'} exportFileName="api-catalog" columns={[
        { key: 'method', label: 'Method', width: '90px' }, { key: 'path', label: 'Request path', width: '350px' },
        { key: 'module', label: 'Module', width: '160px' }, { key: 'purpose', label: 'Purpose', width: '300px' },
      ]} actions={row => <Button size="small" onClick={() => setSelected(row.source)}>Open &amp; test</Button>} />
    </section>
    {selected && document && <ApiExplorer row={selected} document={document} onClose={() => setSelected(null)} onManageKeys={canManageKeys ? () => setKeysOpen(true) : undefined} />}
    {canManageKeys && <AttendanceDevices clientId={clientId} open={keysOpen} onClose={closeKeys} />}
  </RecruitmentViewContext.Provider>
}
