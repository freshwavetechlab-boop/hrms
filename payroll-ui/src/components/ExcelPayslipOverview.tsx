import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Drawer, Empty, Input, Select, Space, Statistic, Table, Tag, Tooltip } from 'antd'
import { getOverviewScopes, previewOverview, type OverviewBatch, type OverviewResult, type OverviewRow } from '../services/excelPayslipOverviewService'
import type { ExcelPayslipClient } from '../services/excelPayslipService'
import ExcelPayslipClientOption from './ExcelPayslipClientOption'
import './ExcelPayslipOverview.css'

const scopeKey = (batch: { clientId: number; batchId: string }) => `${batch.clientId}:${batch.batchId}`
const sourceCurrency = new Intl.NumberFormat('en-IN', { style: 'currency', currency: 'INR', minimumFractionDigits: 2, maximumFractionDigits: 2 })
export default function ExcelPayslipOverview({ open, permittedClients, canConfigure, onClose, onOpen }: {
  open: boolean; permittedClients?: ExcelPayslipClient[]; canConfigure: boolean; onClose: () => void; onOpen: (clientId: number, batchId: string, setup: boolean) => void
}) {
  const [catalog, setCatalog] = useState<OverviewBatch[]>([]), [selected, setSelected] = useState<string[]>([])
  const [clientFilter, setClientFilter] = useState<number>(), [month, setMonth] = useState<string>()
  const [search, setSearch] = useState(''), [readiness, setReadiness] = useState('All'), [masterStatus, setMasterStatus] = useState('All')
  const [overlapOnly, setOverlapOnly] = useState(false)
  const [result, setResult] = useState<OverviewResult | null>(null), [loading, setLoading] = useState(false), [catalogLoading, setCatalogLoading] = useState(false), [error, setError] = useState('')
  const [catalogError, setCatalogError] = useState(''), [catalogRevision, setCatalogRevision] = useState(0)
  const [catalogReady, setCatalogReady] = useState(false)
  const [scrollY, setScrollY] = useState(240), request = useRef(0), controller = useRef<AbortController | null>(null), tableBox = useRef<HTMLDivElement>(null)
  const cancelPending = useCallback(() => { controller.current?.abort(); ++request.current }, [])
  useEffect(() => {
    if (!open) return
    let active = true
    const catalogAbort = new AbortController()
    void Promise.resolve().then(async () => {
      if (!active) return
      setCatalogLoading(true); setCatalogReady(false); setCatalogError(''); setError(''); setResult(null)
      const response = await getOverviewScopes(catalogAbort.signal)
      if (!active) return
      if (response.ok) { setCatalog(response.data); setCatalogReady(true) }
      else { setCatalog([]); setSelected([]); setCatalogError(response.error || 'Unable to load permitted batch metadata.') }
      setCatalogLoading(false)
    })
    return () => { active = false; catalogAbort.abort(); cancelPending() }
  }, [open, catalogRevision, cancelPending])
  useEffect(() => {
    const element = tableBox.current; if (!open || !element) return
    const measure = () => {
      const header = element.querySelector('thead')?.getBoundingClientRect().height || 48
      const pager = element.querySelector('.ant-pagination')?.getBoundingClientRect().height || 32
      setScrollY(Math.max(80, element.clientHeight - header - pager - 24))
    }
    const observer = new ResizeObserver(measure); observer.observe(element); const frame = requestAnimationFrame(measure)
    return () => { observer.disconnect(); cancelAnimationFrame(frame) }
  }, [open, result])
  const clients = useMemo(() => {
    const idsWithBatches = new Set(catalog.map(batch => batch.clientId))
    const available = permittedClients ?? [...new Map(catalog.map(batch => [batch.clientId, { id: batch.clientId, name: batch.clientName, hasSavedBatch: true }])).values()]
    return available.map(client => {
      const hasSavedBatch = typeof client.hasSavedBatch === 'boolean' ? client.hasSavedBatch : catalogReady && idsWithBatches.has(client.id)
      return { value: client.id, label: client.name, hasSavedBatch, className: hasSavedBatch ? 'excel-payslip-client-has-batches' : 'excel-payslip-client-no-batches' }
    })
  }, [permittedClients, catalog, catalogReady])
  const months = useMemo(() => [...new Set(catalog.filter(b => !clientFilter || b.clientId === clientFilter).map(b => b.month))].sort().reverse().map(value => ({ value, label: value })), [catalog, clientFilter])
  const choices = catalog.filter(b => (!clientFilter || b.clientId === clientFilter) && (!month || b.month === month))
  const resetResult = () => { controller.current?.abort(); ++request.current; setLoading(false); setResult(null); setError('') }
  const load = async (page = 1, pageSize = result?.pageSize || 25, filters = { search, readiness, masterStatus, overlapOnly }) => {
    const chosen = selected.map(key => catalog.find(b => scopeKey(b) === key))
    if (chosen.some(b => !b)) { setResult(null); setError('Some chosen batches are no longer in the permitted catalog. Clear/reselect scope before previewing.'); return }
    const scopes = chosen.filter((b): b is OverviewBatch => Boolean(b)).map(({ clientId, batchId }) => ({ clientId, batchId }))
    if (!scopes.length) { setError('Choose saved batches explicitly. No active set is assumed.'); return }
    const token = ++request.current; controller.current?.abort(); const abort = new AbortController(); controller.current = abort
    setLoading(true); setError('')
    const response = await previewOverview({ scopes, ...filters, page, pageSize }, abort.signal)
    if (token !== request.current) return
    if (response.ok && response.data) setResult(response.data); else { setResult(null); setError(response.error || 'Unable to preview the selected scope.') }
    setLoading(false)
  }
  const showOverlaps = (value: boolean) => {
    setOverlapOnly(value)
    void load(1, result?.pageSize || 25, { search, readiness, masterStatus, overlapOnly: value })
  }
  const changeStatus = (kind: 'readiness' | 'masterStatus', value: string) => {
    if (kind === 'readiness') setReadiness(value); else setMasterStatus(value)
    resetResult()
  }
  const columns = [
    { title: 'Employee / source', key: 'employee', width: 220, render: (_: unknown, row: OverviewRow) => <><strong>{row.employeeCode || 'Code not supplied'}</strong><div>{row.employeeName}</div><small>Source row {row.sourceRow}</small></> },
    { title: 'Client / period', key: 'client', width: 190, render: (_: unknown, row: OverviewRow) => <>{row.clientName}<div>{row.month}</div></> },
    { title: 'Batch / version', key: 'batch', width: 260, render: (_: unknown, row: OverviewRow) => <><div>{row.fileName}</div><div>{row.sheetName}</div><small style={{ overflowWrap: 'anywhere' }}>{row.batchId}</small>{row.scopeOverlap && <Tag color="warning">Scope overlap</Tag>}</> },
    { title: 'PDF review readiness', key: 'ready', width: 270, render: (_: unknown, row: OverviewRow) => <><Tag color={row.readiness === 'Ready' ? 'success' : row.readiness === 'Blocked' ? 'error' : 'warning'}>{row.readiness}</Tag>{row.reasons.map(reason => <div key={reason}>{reason}</div>)}</> },
    { title: 'Employee Master', key: 'master', width: 280, render: (_: unknown, row: OverviewRow) => <><Tag color={row.masterStatus === 'Matched' ? 'blue' : row.masterStatus === 'Conflict' ? 'error' : 'default'}>{row.masterStatus}</Tag><div>{row.masterReason}</div><small>{row.setupDecision}</small></> },
    { title: 'Review', key: 'review', fixed: 'right' as const, width: 155, render: (_: unknown, row: OverviewRow) => <Space direction="vertical" size={4}><Button size="small" disabled={loading} onClick={() => onOpen(row.clientId, row.batchId, false)}>Open batch</Button>{canConfigure && <Tooltip title={row.hasCalculationSource ? 'Review employees, salary components/templates, effective dates and attendance options. No direct employee-only import.' : 'This saved batch has no calculation source. Original source/rule review is required.'}><span><Button size="small" disabled={loading || !row.hasCalculationSource} onClick={() => onOpen(row.clientId, row.batchId, true)}>Review Master Setup</Button></span></Tooltip>}</Space> },
  ]
  return <Drawer open={open} onClose={() => { controller.current?.abort(); ++request.current; setLoading(false); onClose() }} title="All permitted clients — selected batch employees" width={1500} contentWrapperStyle={{ maxWidth: '98vw' }} style={{ width: '100%', maxWidth: '100%', minWidth: 0 }} className="excel-payslip-overview">
    <div className="excel-payslip-overview-controls">
      <Alert type="info" showIcon message="Explicit selected scope — no active-batch default is assumed or saved." description="Choose client, period and exact versions. Changing scope filters clears selection. Source rows are not global persons; overlapping versions are shown, not merged." />
      {catalogError && <Alert type="error" showIcon message={catalogError} action={<Button size="small" disabled={catalogLoading} onClick={() => setCatalogRevision(value => value + 1)}>Retry client/batch list</Button>} />}
      {error && <Alert type="error" showIcon message={error} />}
      <Space wrap className="excel-payslip-overview-filters">
        <Select allowClear showSearch optionFilterProp="label" optionLabelProp="label" aria-label="Overview client" placeholder="All permitted clients" style={{ width: 260 }} value={clientFilter} loading={catalogLoading} onChange={value => { setClientFilter(value); setMonth(undefined); setSelected([]); setOverlapOnly(false); resetResult() }}>{clients.map(client => <Select.Option key={client.value} value={client.value} label={client.label} className={client.className}><ExcelPayslipClientOption name={client.label} hasSavedBatch={client.hasSavedBatch} /></Select.Option>)}</Select>
        <Select allowClear aria-label="Overview period" placeholder="All source periods" style={{ width: 180 }} options={months} value={month} onChange={value => { setMonth(value); setSelected([]); setOverlapOnly(false); resetResult() }} />
      </Space>
      <Select mode="multiple" showSearch optionFilterProp="label" aria-label="Overview selected batches" placeholder="Select exact batch versions / source units" maxTagCount="responsive" style={{ width: '100%' }} loading={catalogLoading} value={selected}
        options={choices.map(b => ({ value: scopeKey(b), label: `${b.clientName} · ${b.month} · ${b.fileName} · ${b.sheetName} · ${b.rowCount} rows · ${b.batchId}` }))}
        onChange={value => { setSelected(value); setOverlapOnly(false); resetResult() }} />
      <Space wrap className="excel-payslip-overview-filters">
        <Input.Search aria-label="Overview search" placeholder="Search code, name, client or version" value={search} onChange={event => { setSearch(event.target.value); resetResult() }} onSearch={() => void load()} style={{ width: 280 }} maxLength={120} />
        <Select aria-label="Overview readiness" value={readiness} style={{ width: 170 }} options={['All', 'Ready', 'Review', 'Blocked'].map(value => ({ value, label: value === 'All' ? 'All readiness states' : value }))} onChange={value => changeStatus('readiness', value)} />
        <Select aria-label="Overview master match" value={masterStatus} style={{ width: 190 }} options={['All', 'Matched', 'Unmatched', 'Conflict', 'Missing code'].map(value => ({ value, label: value === 'All' ? 'All master-match states' : value }))} onChange={value => changeStatus('masterStatus', value)} />
        <Checkbox checked={overlapOnly} disabled={loading || !selected.length || catalogLoading} onChange={event => showOverlaps(event.target.checked)}>Only overlapping rows</Checkbox>
        <Button type="primary" loading={loading} disabled={!selected.length || catalogLoading} onClick={() => void load()}>Preview selected scope ({selected.length})</Button>
        <Button disabled={loading || !selected.length} onClick={() => { setSelected([]); setOverlapOnly(false); resetResult() }}>Clear scope</Button>
      </Space>
      <div className="excel-payslip-overview-tiles excel-payslip-overview-money" aria-label="Selected source salary totals">
        {([
          ['Source net pay', result?.totals.sourceNetPay, 'Total take-home amount in saved payslips'],
          ['Source gross earnings', result?.totals.sourceGrossEarnings, 'Gross salary before deductions'],
          ['Source deductions', result?.totals.sourceDeductions, 'Total deductions in saved payslips'],
        ] as const).map(([title, value, description]) => <div key={title}>
          <Statistic title={title} value={value ?? 0} formatter={() => !result ? '—' : typeof value === 'number' && Number.isFinite(value) ? sourceCurrency.format(value) : 'Unavailable'} />
          <small>{description}</small>
        </div>)}
      </div>
      {result && <small className="excel-payslip-overview-money-note">Saved Excel Payslip totals, not bank disbursement confirmation. All {result.totals.sourceRows.toLocaleString('en-IN')} filtered source rows are included, across all pages.{result.totals.monetaryRoundingBasis && <> Totals sum each saved payslip rounded to 2 decimals.</>}</small>}
      {result && result.totals.overlapRows > 0 && <Alert type="warning" showIcon message="Overlapping source rows included" description={`${result.totals.overlapRows} source rows share an employee code or matched master in the same client/period, within or across selected batches. This is a row count, not the number of extra employees. Amounts still include every row; review them before relying on combined salary totals.`} action={<Button disabled={loading} onClick={() => showOverlaps(!overlapOnly)}>{overlapOnly ? 'Show all selected rows' : `Show overlapping rows (${result.totals.overlapRows})`}</Button>} />}
      <div className="excel-payslip-overview-tiles">
        <div><Statistic title="Filtered source rows" value={result?.totals.sourceRows ?? '—'} /><small>{result ? `${result.scopeTotalRows} rows before filters · ${result.scopeBatchCount} chosen batches` : 'Current live count not loaded'}</small></div>
        <div><Statistic title="Ready / review / blocked" value={result ? `${result.totals.readyRows} / ${result.totals.reviewRows} / ${result.totals.blockedRows}` : '—'} /><small>Saved-source PDF checks; not payroll parity</small></div>
        <div aria-label="Selected employee count"><Statistic title="Total employees" value={result?.totals.uniqueScopedCodeGroups ?? '-'} /><small>Unique client + employee code; repeated versions counted once.</small><div><small>{result ? typeof result.totals.uncountedEmployeeRows === 'number' ? `${result.totals.uncountedEmployeeRows.toLocaleString('en-IN')} source rows excluded: missing or conflicting identity.` : 'Identity exclusions unavailable; refresh the preview.' : 'Count appears after preview; identities requiring review are excluded.'}</small></div></div>
        <div><Statistic title="Matched master records" value={result?.totals.matchedMasterRecords ?? '—'} /><small>{result ? `${result.totals.unmatchedRows} unmatched · ${result.totals.conflictRows} conflict · ${result.totals.missingCodeRows} missing-code rows` : 'No comparison has run'}</small></div>
      </div>
      {result && <small>{result.scopeLabel}. Observed {new Date(result.observedAtUtc).toLocaleString()}. Filters and page counts come from the same server preview.</small>}
      {canConfigure && <Alert type="warning" showIcon message="Master Setup is a reviewed configuration workflow, not a direct employee-only transfer." description="It may assign salary components/templates and source-month attendance after separate confirmation. This overview writes nothing and does not activate policies or verify regular payroll parity." />}
    </div>
    <div className="excel-payslip-overview-table" ref={tableBox}>
      <Table<OverviewRow> size="small" rowKey="key" loading={loading} columns={columns} dataSource={result?.rows || []} scroll={{ x: 1455, y: scrollY }}
        locale={{ emptyText: <Empty description={result ? 'No rows match these filters.' : 'Select batches and preview. No live count is assumed.'} /> }}
        pagination={result ? { current: result.page, pageSize: result.pageSize, total: result.totals.sourceRows, showSizeChanger: true, pageSizeOptions: [25, 50, 100], showTotal: total => `${total} filtered source rows`, onChange: (page, pageSize) => void load(page, pageSize) } : false} />
    </div>
  </Drawer>
}
