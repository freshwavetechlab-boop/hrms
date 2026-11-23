import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Drawer, Form, Input, Select, Space, Tag } from 'antd'
import { DownloadOutlined } from '@ant-design/icons'
import DataTable from './DataTable'
import FileDropZone from './FileDropZone'
import type { DocumentRosterRow, DocumentTemplate } from '../services/recruitmentDocumentService'
import { getStandardRosterTemplate, uploadStandardRoster, type RosterExchangeMode, type RosterExchangePreview, type RosterExchangeResult, type RosterExchangeRow, type RosterExchangeTemplate } from '../services/recruitmentRosterExchangeService'
import { downloadStandardRosterTemplate } from '../utils/recruitmentRosterExchangeTemplate'

export default function RecruitmentRosterExchangeDrawer({ clientId, templates, existingRows, initialGroup, mode, onClose, onDone }: {
  clientId: number; templates: DocumentTemplate[]; existingRows: DocumentRosterRow[]; initialGroup: string; mode: RosterExchangeMode;
  onClose: () => void; onDone: (result: RosterExchangeResult) => Promise<void>
}) {
  const [templateId, setTemplateId] = useState(templates[0]?.id || 0)
  const batches = [...new Set(existingRows.filter(row => row.templateId === templateId).map(row => row.sourceGroup))]
  const [group, setGroup] = useState(initialGroup || (mode === 'Update' ? batches.length === 1 ? batches[0] : '' : 'Employee list'))
  const [schema, setSchema] = useState<RosterExchangeTemplate | null>(null), [loading, setLoading] = useState(false)
  const [file, setFile] = useState<File | null>(null), [preview, setPreview] = useState<RosterExchangePreview | null>(null), [selected, setSelected] = useState<number[]>([])
  const [result, setResult] = useState<RosterExchangeResult | null>(null), [busy, setBusy] = useState(false), [error, setError] = useState(''), [phase, setPhase] = useState('')
  const review = useRef<HTMLElement | null>(null), generation = useRef(0)
  const scopeGroup = mode === 'Update' ? group : ''
  useEffect(() => {
    const token = ++generation.current; setSchema(null); setPreview(null); setSelected([]); setFile(null); setResult(null); setError('')
    if (!templateId || mode === 'Update' && !scopeGroup) { setLoading(false); return }
    setLoading(true)
    void getStandardRosterTemplate({ clientId, templateId, sourceGroup: scopeGroup, mode }).then(response => {
      if (token !== generation.current) return
      if (!response.ok || !response.data) setError(response.error || 'Unable to load the standard template.')
      else setSchema(response.data)
    }).catch(e => { if (token === generation.current) setError(e instanceof Error ? e.message : 'Unable to load the standard template.') })
      .finally(() => { if (token === generation.current) setLoading(false) })
    return () => { generation.current++ }
  }, [clientId, templateId, scopeGroup, mode])
  useEffect(() => { if (preview || result) review.current?.scrollIntoView({ block: 'start', behavior: 'smooth' }) }, [preview, result])
  const settings = { clientId, templateId, sourceGroup: group.trim(), mode, sourceRows: selected, previewToken: preview?.previewToken || '' }
  const buildPreview = async () => {
    if (!file || !schema || !group.trim() || busy) return
    setBusy(true); setError(''); setPhase('Checking Profile Codes, current versions and infotype values…'); setPreview(null); setResult(null)
    try {
      const data = await uploadStandardRoster<RosterExchangePreview>('preview', file, settings)
      if (!Array.isArray(data.rows)) throw new Error('The preview response is incomplete. Your file is retained; retry shortly.')
      setPreview(data); setSelected(data.rows.filter(row => row.action === 'Update' || row.action === 'Insert').map(row => row.sourceRow))
    } catch (e) { setError(e instanceof Error ? e.message : 'Unable to preview the standard workbook.') }
    finally { setBusy(false); setPhase('') }
  }
  const apply = async () => {
    if (!file || !preview || busy) return
    setBusy(true); setError(''); setPhase(`${mode === 'Update' ? 'Updating' : 'Inserting'} ${selected.length} reviewed rows; unmatched/unselected rows will be skipped…`)
    try {
      const data = await uploadStandardRoster<RosterExchangeResult>('import', file, settings)
      setResult(data); window.dispatchEvent(new Event('employees:changed'))
    } catch (e) { setError(e instanceof Error ? e.message : 'Unable to save the reviewed workbook. Preview again before retrying.') }
    finally { setBusy(false); setPhase('') }
  }
  const rows = result?.items || preview?.rows || []
  const skipped = rows.filter(row => row.action === 'Skip').length
  return <Drawer open title={mode === 'Update' ? 'Update Corrected Version' : 'Insert New Profiles'} className="document-roster-import" width="min(1300px, 98vw)" closable={!busy} maskClosable={!busy}
    onClose={() => { if (!busy) result ? void onDone(result) : onClose() }} footer={<>
      {error && <Alert className="document-roster-import-feedback" type="error" showIcon message={error} />}
      {phase && <Alert className="document-roster-import-feedback" type="info" showIcon message={phase} />}
      <Space wrap>{result ? <Button type="primary" onClick={() => void onDone(result)}>Done</Button> : <>
        <Button disabled={busy} onClick={onClose}>Cancel</Button>
        <Button disabled={busy || loading || !schema || !file || !group.trim()} onClick={() => void buildPreview()}>Preview standard workbook</Button>
        <Button type="primary" loading={busy} disabled={busy || !preview} onClick={() => void apply()}>{mode === 'Update' ? 'Update' : 'Insert'} selected ({selected.length}) · Skip {skipped + (preview?.rows.filter(row => ['Insert', 'Update'].includes(row.action) && !selected.includes(row.sourceRow)).length || 0)}</Button>
      </>}</Space></>}>
    <Alert type="info" showIcon message={mode === 'Update' ? 'Download the current data, edit it and upload the same standard workbook' : 'Use the standard employee template for every new profile'}
      description="One sheet for Employee Master details, bank details and Basic pay. Salary templates handle allowances, increments and derived pay. Blank keeps existing data; 0 sets zero. Unknown update codes are skipped." />
    <Form layout="vertical" className="document-roster-fields" disabled={busy || !!result}>
      <Form.Item label="Offer document template" required><Select aria-label="Standard offer template" value={templateId} options={templates.map(t => ({ value: t.id, label: t.templateName }))} onChange={value => { setTemplateId(value); if (mode === 'Update') { const groups = [...new Set(existingRows.filter(r => r.templateId === value).map(r => r.sourceGroup))]; setGroup(groups.length === 1 ? groups[0] : '') } }} /></Form.Item>
      <Form.Item label="Import batch" required>{mode === 'Update' ? <Select aria-label="Corrected version import batch" placeholder="Select the saved list to update" value={group || undefined} options={batches.map(value => ({ value, label: value }))} onChange={setGroup} />
        : <Input aria-label="Standard insertion batch" maxLength={180} value={group} onChange={e => { setGroup(e.target.value); setPreview(null); setSelected([]) }} />}</Form.Item>
    </Form>
    <div className="document-roster-template-download"><div><strong>{mode === 'Update' ? `1. Download current version (${schema?.rows.length || 0} rows)` : '1. Download the blank standard template'}</strong><p>Fill the single Employees sheet and keep its headers. For updates, keep Profile Code unchanged.</p></div>
      <Button icon={<DownloadOutlined />} loading={loading} disabled={!schema || busy || !!result || !group.trim() || mode === 'Update' && !schema.rows.length} onClick={() => { if (schema) downloadStandardRosterTemplate({ ...schema, sourceGroup: group.trim() }, mode) }}>{mode === 'Update' ? 'Download current version' : 'Download standard template'}</Button></div>
    {!result && <FileDropZone accept=".xlsx" title="2. Upload the completed standard workbook" hint="Excel .xlsx · up to 10 MB · Employees sheet" fileName={file?.name || ''} onFile={upload => { if (!busy) { setFile(upload); setPreview(null); setSelected([]); setError('') } }} />}
    {(preview || result) && <section ref={review} aria-label="Standard employee import review">
      {result ? <Alert type={result.skipped ? 'warning' : 'success'} showIcon message={`${result.updated} updated · ${result.inserted} inserted · ${result.unchanged} unchanged · ${result.skipped} skipped`} description="Review skipped rows below. Corrected letters are reviewed and released separately." />
        : <Space wrap style={{ marginBlock: 12 }}><Tag>{rows.length} rows</Tag><Tag color="green">{selected.length} selected</Tag><Tag color="orange">{skipped} skipped — other rows can continue</Tag></Space>}
      <DataTable rows={rows} getRowId={row => row.sourceRow} title="Profile Code matching and changes" exportFileName="employee-standard-review" rowSelection={result ? undefined : { selectedRowKeys: selected, onChange: keys => setSelected(keys.map(Number)), getCheckboxProps: row => ({ disabled: busy || !['Update', 'Insert'].includes(row.action) }) }} columns={[
        { key: 'sourceRow', label: 'Excel row', width: 80 }, { key: 'profileCode', label: 'Profile Code', width: 170 },
        { key: 'employeeName', label: 'Employee', width: 210 }, { key: 'action', label: 'Action', width: 100, render: row => <Tag color={row.action === 'Skip' ? 'orange' : 'green'}>{row.action}</Tag> },
        { key: 'reason', label: 'Review / skip reason', width: 360 }, { key: 'changedFields', label: 'Changed fields', width: 300, value: (row: RosterExchangeRow) => row.changedFields.join(', ') },
        { key: 'employeeCode', label: 'Employee Code', width: 160 },
      ]} />
    </section>}
  </Drawer>
}
