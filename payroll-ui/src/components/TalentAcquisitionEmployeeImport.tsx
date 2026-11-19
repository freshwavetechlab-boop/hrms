import { useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Modal, Space, Tag } from 'antd'
import { ImportOutlined, ReloadOutlined } from '@ant-design/icons'
import DataTable, { type Column } from './DataTable'
import { getRecruitmentJoiningCandidates, importRecruitmentJoiningCandidates, type RecruitmentJoiningCandidate, type RecruitmentJoiningImportResult } from '../services/recruitmentJoiningService'
import './TalentAcquisitionEmployeeImport.css'

type Props = { clientId: number; clientName: string; onImported: () => Promise<void> }
const ready = (row: RecruitmentJoiningCandidate, clientId: number) => row.clientId === clientId && row.acceptanceRecorded && ['Offer', 'Roster'].includes(row.sourceType) && row.importStatus === 'Ready' && !row.employeeId
const date = (value: string) => value ? value.slice(0, 10).split('-').reverse().join('/') : 'Not supplied'

export default function TalentAcquisitionEmployeeImport({ clientId, clientName, onImported }: Props) {
  const [open, setOpen] = useState(false), [loading, setLoading] = useState(false), [busy, setBusy] = useState(false)
  const [rows, setRows] = useState<RecruitmentJoiningCandidate[]>([]), [visibleRows, setVisibleRows] = useState<RecruitmentJoiningCandidate[]>([])
  const [selected, setSelected] = useState<string[]>([]), [revision, setRevision] = useState(0)
  const [error, setError] = useState(''), [result, setResult] = useState<RecruitmentJoiningImportResult | null>(null)
  const generation = useRef(0), importing = useRef(false), previousClient = useRef(clientId)
  useEffect(() => {
    const changedClient = previousClient.current !== clientId
    if (changedClient) { previousClient.current = clientId; setOpen(false) }
    const token = ++generation.current
    setRows([]); setVisibleRows([]); setSelected([]); setError(''); setResult(null)
    if (!open || !clientId || changedClient) { setLoading(false); return }
    setLoading(true)
    void getRecruitmentJoiningCandidates(clientId).then(response => {
      if (token !== generation.current) return
      if (!response.ok) { setError(response.error || 'Unable to load candidates from Talent Acquisition.'); return }
      const scoped = response.data.filter(row => row.clientId === clientId && (row.acceptanceRecorded || row.importStatus === 'Imported'))
      setRows(scoped); setVisibleRows(scoped); setSelected(scoped.filter(row => ready(row, clientId)).map(row => row.rowKey))
    }).catch(failure => { if (token === generation.current) setError(failure instanceof Error ? failure.message : 'Unable to load joining candidates.') })
      .finally(() => { if (token === generation.current) setLoading(false) })
    return () => { generation.current++ }
  }, [open, clientId, revision])
  const selectedRows = rows.filter(row => selected.includes(row.rowKey) && ready(row, clientId) && visibleRows.some(visible => visible.rowKey === row.rowKey))
  const eligibleVisible = rows.filter(row => ready(row, clientId) && visibleRows.some(visible => visible.rowKey === row.rowKey))
  const columns = useMemo<Column<RecruitmentJoiningCandidate>[]>(() => [
    { key: 'candidateName', label: 'Candidate', render: row => <><strong>{row.candidateName}</strong><small>{row.candidateCode}</small></> },
    { key: 'positionTitle', label: 'Position' }, { key: 'jobLocation', label: 'Location' },
    { key: 'proposedJoiningDate', label: 'Joining date', render: row => date(row.proposedJoiningDate) },
    { key: 'offeredCtc', label: 'Annual CTC', render: row => `${row.currency || 'INR'} ${Number(row.offeredCtc || 0).toLocaleString('en-IN')}` },
    { key: 'importStatus', label: 'Employee Master', render: row => <Tag color={row.employeeId ? 'green' : row.importStatus === 'Review' ? 'orange' : 'blue'}>{row.employeeId ? 'Already imported' : row.importStatus}</Tag> },
    { key: 'employeeCode', label: 'Employee code' }, { key: 'issue', label: 'Details', wrap: true },
  ], [])
  const importSelected = async () => {
    if (importing.current || loading || !clientId || !selectedRows.length) return
    importing.current = true; setBusy(true); setError(''); setResult(null)
    const token = generation.current
    try {
      const response = await importRecruitmentJoiningCandidates({ clientId,
        offerIds: selectedRows.filter(row => row.sourceType === 'Offer').map(row => row.offerId),
        rosterRowIds: selectedRows.filter(row => row.sourceType === 'Roster').map(row => row.rosterRowId || row.sourceId),
      })
      if (token !== generation.current) { if (response.ok && response.data && (response.data.imported || response.data.alreadyImported)) await onImported(); return }
      if (!response.ok || !response.data) { setError(response.error || 'The selected candidates could not be imported.'); return }
      const imported = response.data
      const resultFor = (row: RecruitmentJoiningCandidate) => imported.items.find(item => item.rowKey ? item.rowKey === row.rowKey
        : row.sourceType === 'Roster' ? item.sourceType === 'Roster' && (item.rosterRowId || item.sourceId) === (row.rosterRowId || row.sourceId)
          : item.sourceType !== 'Roster' && item.offerId === row.offerId)
      setResult(imported)
      setRows(current => current.map(row => {
        const item = resultFor(row)
        return item ? { ...row, importStatus: item.status === 'Review' ? 'Review' : 'Imported', employeeId: item.employeeId || row.employeeId, employeeCode: item.employeeCode || row.employeeCode, issue: item.message } : row
      }))
      setSelected(current => current.filter(key => !rows.some(row => row.rowKey === key && !!resultFor(row))))
      if (imported.imported || imported.alreadyImported) await onImported()
    } catch (failure) { if (token === generation.current) setError(failure instanceof Error ? failure.message : 'The selected candidates could not be imported.') }
    finally { importing.current = false; setBusy(false) }
  }
  return <>
    <Button icon={<ImportOutlined />} disabled={!clientId || busy} title={!clientId ? 'Select a client first.' : undefined} onClick={() => setOpen(true)}>Import selected from Talent Acquisition</Button>
    <Modal open={open} title="Import selected from Talent Acquisition" width={1180} className="talent-employee-import-modal" maskClosable={!busy} closable={!busy} onCancel={() => { if (!busy) setOpen(false) }} footer={<Space wrap><Button disabled={busy} onClick={() => setOpen(false)}>Close</Button><Button type="primary" icon={<ImportOutlined />} loading={busy} disabled={loading || !selectedRows.length} onClick={() => void importSelected()}>Import selected ({selectedRows.length})</Button></Space>}>
      <div className="talent-employee-import-context"><strong>{clientName}</strong><span>{rows.filter(row => ready(row, clientId)).length} ready · {rows.filter(row => !!row.employeeId).length} imported</span></div>
      <p className="talent-employee-import-note">Deselect anyone who has not joined, then import the accepted candidates. Previously imported employees stay listed.</p>
      {error && <Alert type="error" showIcon message={error} />}
      {result && <Alert type={result.review ? 'warning' : 'success'} showIcon message={`${result.imported} imported · ${result.alreadyImported} already imported${result.review ? ` · ${result.review} need review` : ''}`} description={result.review ? 'The Details column shows the reason for each candidate that needs review. Successful imports are kept.' : 'Employee Master has been refreshed.'} />}
      <div className="talent-employee-import-controls"><Space wrap><Button size="small" disabled={loading || busy || !eligibleVisible.length} onClick={() => setSelected(current => [...new Set([...current, ...eligibleVisible.map(row => row.rowKey)])])}>Select all matching</Button><Button size="small" disabled={busy || !selected.length} onClick={() => setSelected([])}>Clear selection</Button><span>{selectedRows.length} selected</span></Space><Button icon={<ReloadOutlined />} size="small" disabled={busy} loading={loading} onClick={() => setRevision(value => value + 1)}>Refresh</Button></div>
      <div className="talent-employee-import-table"><DataTable key={`joining:${clientId}`} fillHeight rows={rows} columns={columns} getRowId={row => row.rowKey} loading={loading} title="Accepted / Joining candidates" rowLabel="candidates" exportFileName="talent-acquisition-joining-candidates" onVisibleRowsChange={setVisibleRows} emptyText="No accepted offers are waiting for this client. Select released offers in Talent Acquisition and mark them accepted first." rowSelection={{ selectedRowKeys: rows.filter(row => selected.includes(row.rowKey) && ready(row, clientId)).map(row => row.rowKey), onChange: keys => setSelected(keys.map(String).filter(key => rows.some(row => row.rowKey === key && ready(row, clientId)))), getCheckboxProps: row => ({ disabled: busy || !ready(row, clientId) }) }} /></div>
    </Modal>
  </>
}
