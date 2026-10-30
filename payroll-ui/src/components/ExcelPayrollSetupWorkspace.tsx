import { useCallback, useEffect, useState } from 'react'
import { Alert, Button, Drawer, Space, Tag } from 'antd'
import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import { getPayrollSetupContext, type PayrollSetupContext, type PayrollSetupResult } from '../services/excelPayrollSetupService'
import ExcelPayrollSetup from './ExcelPayrollSetup'
import EmployeeResignationAction from './EmployeeResignationAction'
import DataTable from './DataTable'
import './ExcelPayrollSetup.css'

type Props = { clientId: number; batch: ExcelPayslipBatch; rowIds: string[]; onClose: () => void; onApplied: (result: PayrollSetupResult) => void }
export default function ExcelPayrollSetupWorkspace({ clientId, batch, rowIds, onClose, onApplied }: Props) {
  const [context, setContext] = useState<PayrollSetupContext | null>(null), [loading, setLoading] = useState(true), [error, setError] = useState('')
  const [group, setGroup] = useState('Existing'), [selected, setSelected] = useState<string[]>([]), [cohort, setCohort] = useState<string[] | null>(null)
  const [missingSelection, setMissingSelection] = useState<string[]>([])
  const load = useCallback(async () => {
    setLoading(true); setError('')
    try {
      const result = await getPayrollSetupContext(clientId, batch.id)
      if (!result.ok || !result.data) throw Error(result.error || 'Unable to inspect this saved batch.')
      setContext(result.data)
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Unable to inspect payroll setup.') }
    finally { setLoading(false) }
  }, [clientId, batch.id])
  useEffect(() => { void load() }, [load])
  const rows = context?.roster?.rows.filter(row => rowIds.includes(row.rowId)) || []
  const visible = rows.filter(row => group === 'All' || row.status === group)
  const pending = visible.filter(row => row.status !== 'Applied')
  const selection = selected.filter(id => pending.some(row => row.rowId === id))
  const missing = context?.roster?.missingEmployees || []
  const exited = missing.filter(row => missingSelection.includes(String(row.employeeId))).map(row => ({ id: row.employeeId, employeeCode: row.employeeCode, name: row.employeeName }))
  const configure = (ids: string[]) => { setSelected([]); setCohort(ids.slice(0, 100)) }
  return <>
    <Drawer open title={`Master setup · ${batch.month}`} width="min(1160px, 98vw)" className="excel-payroll-setup" onClose={onClose} footer={<Space wrap className="excel-payroll-setup-footer">
      <Button disabled={loading || !!cohort} onClick={onClose}>Close</Button>
      <Button disabled={loading || !pending.length || !!cohort} onClick={() => configure(pending.map(row => row.rowId))}>Configure next {Math.min(100, pending.length)}{group === 'All' ? '' : ` ${group.toLowerCase()}`}</Button>
      <Button type="primary" disabled={loading || !selection.length || !!cohort} onClick={() => configure(selection)}>Configure selected ({selection.length})</Button>
    </Space>}>
      {error && <Alert showIcon type="error" message={error} action={<Button onClick={() => void load()}>Retry</Button>} />}
      <Alert type="info" showIcon message="Complete Master setup from any saved Excel batch" description="Review existing employees, new employees and uncertain matches separately. Each reviewed group creates salary components/templates, employee salary assignments and source-month attendance. Groups contain up to 100 employees; applied rows are retained and excluded from the next group." />
      {context && !context.roster && <Alert type="warning" showIcon message="Restart the API with this code to load payroll readiness groups." />}
      <Space wrap style={{ marginBlock: 12 }}>
        {['Existing', 'New', 'Review', 'Applied', 'All'].map(status => <Button key={status} type={status === group ? 'primary' : 'default'} onClick={() => { setGroup(status); setSelected([]) }}>{status === 'Review' ? 'Needs review' : status} ({status === 'All' ? rows.length : rows.filter(row => row.status === status).length})</Button>)}
        <Button disabled={loading} onClick={() => void load()}>Refresh readiness</Button>
        {context?.lastImport && ['Draft', 'Kept'].includes(context.lastImport.status) && <Button disabled={loading} onClick={() => configure(context.lastImport!.savedRequest.rowIds)}>Resume saved draft</Button>}
      </Space>
      <DataTable rows={visible} loading={loading} title="Employee Master readiness" getRowId={row => row.rowId} exportDisabled rowSelection={{ selectedRowKeys: selection, onChange: keys => setSelected(keys.map(String)), getCheckboxProps: row => ({ disabled: row.status === 'Applied' || loading }) }} columns={[
        { key: 'sourceRow', label: 'Excel row', width: 90 }, { key: 'employeeName', label: 'Employee' }, { key: 'employeeCode', label: 'Master / proposed code' },
        { key: 'status', label: 'Status', render: row => <Tag color={row.status === 'Applied' ? 'green' : row.status === 'Review' ? 'orange' : row.status === 'New' ? 'blue' : 'default'}>{row.status}</Tag> }, { key: 'message', label: 'Review', wrap: true },
      ]} actionsWidth={215} actions={row => row.status === 'Applied' ? <Space direction="vertical" size={4}><Button size="small" disabled={!context?.appliedSetups?.some(ids => ids.includes(row.rowId))} onClick={() => configure(context!.appliedSetups!.find(ids => ids.includes(row.rowId))!)}>View saved setup</Button>{context?.salaryCorrectionSupported === true && <Button size="small" onClick={() => configure([row.rowId])}>Review salary correction</Button>}</Space> : <Button size="small" onClick={() => configure([row.rowId])}>Review row</Button>} />
      {selection.length > 100 && <Alert type="info" message="The first 100 selected employees will open. Continue with the remaining rows after applying that group." />}
      {!!missing.length && <details className="excel-payroll-missing"><summary>Active master employees absent from this sheet ({missing.length})</summary>
        <p>Absence from one sheet does not prove resignation. Select only reviewed exits; the dialog suggests each employee's latest payroll month-end. Existing history remains available.</p>
        <Space wrap><Button onClick={() => setMissingSelection(missing.map(row => String(row.employeeId)))}>Select all missing</Button><Button onClick={() => setMissingSelection([])}>Clear selection</Button><EmployeeResignationAction clientId={clientId} employees={exited} disabled={loading} onSaved={async () => { setMissingSelection([]); await load() }} /></Space>
        <DataTable rows={missing} getRowId={row => String(row.employeeId)} exportDisabled title="Review missing employees" rowSelection={{ selectedRowKeys: missingSelection, onChange: keys => setMissingSelection(keys.map(String)) }} columns={[{ key: 'employeeCode', label: 'Code' }, { key: 'employeeName', label: 'Employee' }, { key: 'workLocation', label: 'Work location' }]} />
      </details>}
    </Drawer>
    {cohort && <ExcelPayrollSetup key={cohort.join('|')} clientId={clientId} batch={batch} rowIds={cohort} onClose={() => { setCohort(null); void load() }} onApplied={result => { onApplied(result); void load() }} />}
  </>
}
