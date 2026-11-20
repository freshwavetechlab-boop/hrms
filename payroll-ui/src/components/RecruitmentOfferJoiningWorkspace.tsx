import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Alert, Button, Segmented, Space, Tag } from 'antd'
import { CheckOutlined, ImportOutlined, ReloadOutlined } from '@ant-design/icons'
import { useNavigate } from 'react-router-dom'
import { useAuthSession } from './AuthGate'
import { releasedForEmployeeMaster, saveOnboardingSelection } from '../utils/employeeOnboardingSelection'
import DataTable, { type Column } from './DataTable'
import { PageHeaderPortal } from './layout/AppPageHeader'
import { useRecruitmentPreference } from '../hooks/useRecruitmentPreferences'
import { acceptRecruitmentJoiningOffers, getRecruitmentJoiningCandidates, type RecruitmentJoiningAcceptResult, type RecruitmentJoiningCandidate } from '../services/recruitmentJoiningService'
import { joiningAcceptanceSelection, joiningRecords, releasedJoiningRecords, type JoiningRecord } from '../utils/recruitmentJoiningSelection'
import './RecruitmentOfferJoiningWorkspace.css'

const date = (value: string) => value ? value.slice(0, 10) : '—'
const columns: Column<JoiningRecord>[] = [
  { key: 'candidateName', label: 'Candidate', wrap: true },
  { key: 'candidateCode', label: 'Candidate code' },
  { key: 'clientName', label: 'Client', wrap: true },
  { key: 'positionTitle', label: 'Job', wrap: true },
  { key: 'jobLocation', label: 'Location', wrap: true },
  { key: 'proposedJoiningDate', label: 'Joining date', render: row => date(row.proposedJoiningDate) },
  { key: 'sourceType', label: 'Offer source', render: row => row.sourceType === 'Roster' ? 'Document roster' : 'Talent Acquisition' },
  { key: 'offerStatus', label: 'Offer status' },
  { key: 'importStatus', label: 'Employee Master', render: row => <Tag color={row.importStatus === 'Imported' ? 'green' : row.importStatus === 'Review' ? 'orange' : 'blue'}>{row.importStatus === 'Imported' ? 'Already imported' : row.importStatus === 'Ready' ? 'Ready to import' : row.importStatus}</Tag> },
  { key: 'employeeCode', label: 'Employee code' },
  { key: 'issue', label: 'Details', wrap: true },
]

export default function RecruitmentOfferJoiningWorkspace({ clientId, offers, onChanged, canAccept }: { clientId: number; offers: ReactNode; onChanged: () => Promise<void>; canAccept: boolean }) {
  const session = useAuthSession(), navigate = useNavigate()
  const canImport = session?.user.permissions.includes('employees.manage') || false
  const [pane, setPane] = useRecruitmentPreference('offerJoiningPane', 'offers')
  const [selected, setSelected] = useRecruitmentPreference<string[]>(`offerAcceptanceSelection:${clientId}`, [])
  const [rows, setRows] = useState<RecruitmentJoiningCandidate[]>([]), [visible, setVisible] = useState<string[]>([])
  const [loading, setLoading] = useState(false), [busy, setBusy] = useState(false), [error, setError] = useState('')
  const [result, setResult] = useState<RecruitmentJoiningAcceptResult | null>(null)
  const generation = useRef(0), accepting = useRef(false)
  const refresh = useCallback(async () => {
    if (!clientId) { setRows([]); setLoading(false); return }
    const token = ++generation.current; setLoading(true)
    try {
      const response = await getRecruitmentJoiningCandidates(clientId)
      if (token !== generation.current) return
      if (response.ok) { setRows(response.data || []); setError('') }
      else setError(response.error || 'Unable to load released offers and joining candidates.')
    } catch { if (token === generation.current) setError('Unable to load released offers and joining candidates.') }
    finally { if (token === generation.current) setLoading(false) }
  }, [clientId])
  useEffect(() => { void refresh(); return () => { generation.current++ } }, [refresh])
  const records = useMemo(() => joiningRecords(rows, clientId), [rows, clientId])
  const released = useMemo(() => releasedJoiningRecords(records), [records])
  const joining = useMemo(() => records.filter(row => row.acceptanceRecorded || row.importStatus === 'Imported'), [records])
  const selection = useMemo(() => joiningAcceptanceSelection(records, selected, visible, clientId), [records, selected, visible, clientId])
  const selectedCount = selection.offerIds.length + selection.rosterRowIds.length
  const paneRecords = pane === 'joining' ? joining : released
  const importSelection = paneRecords.filter(row => selected.includes(row.id) && visible.includes(row.id) && releasedForEmployeeMaster(row, clientId))
  const importIntoMaster = () => {
    if (!clientId || !canImport || !importSelection.length) return
    saveOnboardingSelection(sessionStorage, session?.user.id || 0, clientId, 'offers', importSelection.map(row => row.id))
    navigate(`/employees/master?clientId=${clientId}&source=talent-acquisition`)
  }
  const visibleChanged = useCallback((current: JoiningRecord[]) => setVisible(previous => {
    const next = current.map(row => row.id)
    return previous.length === next.length && previous.every((id, i) => id === next[i]) ? previous : next
  }), [])
  const accept = async () => {
    if (accepting.current || busy || !selectedCount || !canAccept) return
    accepting.current = true
    const token = generation.current
    setBusy(true); setError(''); setResult(null)
    try {
      const response = await acceptRecruitmentJoiningOffers(selection)
      if (token !== generation.current) return
      if (!response.ok || !response.data) { setError(response.error || 'Unable to record the selected acceptances.'); return }
      setResult(response.data)
      const submitted = new Set([...selection.offerIds.map(id => `offer:${id}`), ...selection.rosterRowIds.map(id => `roster:${id}`)])
      const review = new Set(response.data.items.filter(row => row.status === 'Review').map(row => row.rowKey || (row.sourceType === 'Roster' ? `roster:${row.sourceId}` : `offer:${row.offerId}`)))
      setSelected(current => current.filter(id => !submitted.has(id) || review.has(id)))
      await Promise.all([refresh(), onChanged()])
      if (response.data.accepted + response.data.alreadyAccepted > 0) setPane('joining')
    } catch { setError('Unable to record the selected acceptances. Refresh to check their latest status before retrying.') }
    finally { accepting.current = false; setBusy(false) }
  }
  const currentPane = ['offers', 'released', 'joining'].includes(pane) ? pane : 'offers'
  return <section className="recruitment-offer-joining-workspace">
    <PageHeaderPortal slot="recruitment-page-controls"><Space wrap>
      {currentPane !== 'offers' && canImport && <Button type="primary" icon={<ImportOutlined />} disabled={!importSelection.length || loading || busy} onClick={importIntoMaster}>Import selected into Employee Master ({importSelection.length})</Button>}
      {currentPane === 'released' && canAccept && <Button icon={<CheckOutlined />} disabled={!selectedCount || loading} loading={busy} onClick={() => void accept()}>Mark selected accepted ({selectedCount})</Button>}
      {currentPane !== 'offers' && <Button icon={<ReloadOutlined />} disabled={busy} loading={loading} onClick={() => void refresh()}>Refresh</Button>}
    </Space></PageHeaderPortal>
    <Segmented className="recruitment-offer-joining-tabs" aria-label="Offer and joining lists" value={currentPane} disabled={busy} options={[
      { value: 'offers', label: 'All offers' }, { value: 'released', label: `Released letters (${released.length})` }, { value: 'joining', label: `Selected for joining (${joining.length})` },
    ]} onChange={value => { setPane(String(value)); setVisible([]) }} />
    {error && <Alert type="error" showIcon message={error} />}
    {!clientId && currentPane !== 'offers' && <Alert type="info" showIcon message="Select a client to review released letters and joining candidates." />}
    {result && <Alert type={result.review ? 'warning' : 'success'} showIcon closable onClose={() => setResult(null)} message={`${result.accepted} accepted · ${result.alreadyAccepted} already recorded · ${result.review} need review`} description={result.review ? result.items.filter(item => item.status === 'Review').map((item, index) => <div key={index}>{item.candidateName || 'Candidate'}: {item.message}</div>) : undefined} />}
    {currentPane === 'offers' ? offers : <>
      {currentPane === 'released' ? <>
        <div className="recruitment-offer-joining-toolbar"><span>Import released letters into Employee Master for ESS. Record candidate acceptance separately when needed.</span><Button size="small" disabled={busy || loading || !visible.length || !(canAccept || canImport)} onClick={() => setSelected(current => [...new Set([...current, ...visible])])}>Select all shown</Button><Button size="small" disabled={busy || !selected.length} onClick={() => setSelected([])}>Clear selection</Button></div>
        <DataTable key={`released:${clientId}`} rows={released} columns={columns} title="Released offer letters" rowLabel="offers" fillHeight loading={loading} onVisibleRowsChange={visibleChanged} exportFileName="released-offer-letters" rowSelection={{ selectedRowKeys: selected, onChange: keys => setSelected(keys.map(String)), getCheckboxProps: () => ({ disabled: busy || !(canAccept || canImport) }) }} emptyText="No released offers are waiting for acceptance." />
      </> : <>
        <div className="recruitment-offer-joining-toolbar"><span>Select joining candidates to import into Employee Master. Already imported employees stay linked.</span><Button size="small" disabled={busy || loading || !canImport || !visible.length} onClick={() => setSelected(current => [...new Set([...current, ...visible.filter(id => joining.some(row => row.id === id && releasedForEmployeeMaster(row, clientId)))])])}>Select all shown</Button><Button size="small" disabled={busy || !selected.length} onClick={() => setSelected([])}>Clear selection</Button></div>
        <DataTable key={`joining:${clientId}`} rows={joining} columns={columns} title="Selected for joining" rowLabel="candidates" fillHeight loading={loading} onVisibleRowsChange={visibleChanged} exportFileName="selected-for-joining" rowSelection={canImport ? { selectedRowKeys: selected, onChange: keys => setSelected(keys.map(String)), getCheckboxProps: row => ({ disabled: busy || !releasedForEmployeeMaster(row, clientId) }) } : undefined} emptyText="Accepted candidates will appear here after their offers are confirmed." />
      </>}
    </>}
  </section>
}
