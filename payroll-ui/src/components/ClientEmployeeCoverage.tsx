import { useMemo, useState } from 'react'
import { Button, Modal, Segmented, Table, Tag } from 'antd'
import { Link } from 'react-router-dom'
import DataTable from './DataTable'
import type { CoverageCheck, CoverageStatus } from '../utils/clientConfigCoverage'

type Filter = CoverageStatus | 'All'
const statusColor = (status: CoverageStatus) => status === 'Covered' ? 'success' : status === 'Review' ? 'warning' : 'error'

export default function ClientEmployeeCoverage({ checks, total, active, asOf, employeeDataAvailable }: { checks: CoverageCheck[]; total: number; active: number; asOf: string; employeeDataAvailable: boolean }) {
  const [selectedKey, setSelectedKey] = useState('')
  const [filter, setFilter] = useState<Filter>('Uncovered')
  const selected = checks.find(row => row.key === selectedKey)
  const rows = useMemo(() => checks.map(check => ({ ...check,
    covered: check.results.filter(row => row.status === 'Covered').length,
    uncovered: check.results.filter(row => row.status === 'Uncovered').length,
    review: check.results.filter(row => row.status === 'Review').length
  })), [checks])
  const open = (key: string, status: Filter) => { setSelectedKey(key); setFilter(status) }
  const count = (row: typeof rows[number], status: CoverageStatus, value: number) => row.unavailable || row.disabled ? <span>-</span>
    : <Button type="link" className={`coverage-count ${status.toLowerCase()}`} disabled={!value} aria-label={`${row.title}: ${value} ${status.toLowerCase()} employees`} onClick={() => open(row.key, status)}>{value}</Button>

  return <section id="employee-coverage" className="config-check-section employee-coverage" aria-label="Employee coverage">
    <div className="config-section-heading"><h3>Employee coverage</h3><span>As of {asOf}</span></div>
    {employeeDataAvailable ? <p><strong>{total}</strong> employee records; <strong>{active}</strong> active employees checked; <strong>{total - active}</strong> inactive excluded.</p> : <p>Employee records could not be loaded. Coverage counts are unavailable; run the check again after resolving access or connectivity.</p>}
    <p>Counts are unique within each check. The same employee can have gaps in several checks; do not add those counts together. Covered means the listed setup is present or applicable, not that payroll calculations are verified.</p>
    <Table rowKey="key" dataSource={rows} pagination={false} size="small" scroll={{ x: 850 }} columns={[
      { title: 'Check', key: 'title', width: 420, render: (_, row) => <div><strong>{row.title}</strong>{row.unavailable ? <Tag>Unavailable</Tag> : row.disabled ? <Tag>Not enabled</Tag> : null}<p>{row.description}</p></div> },
      { title: 'Total', key: 'total', width: 75, render: (_, row) => row.unavailable || row.disabled ? '-' : active },
      { title: 'Covered', key: 'covered', width: 95, render: (_, row) => count(row, 'Covered', row.covered) },
      { title: 'Uncovered', key: 'uncovered', width: 100, render: (_, row) => count(row, 'Uncovered', row.uncovered) },
      { title: 'Review', key: 'review', width: 85, render: (_, row) => count(row, 'Review', row.review) },
      { title: 'Action', key: 'action', width: 130, render: (_, row) => <Link to={row.route}>Open setup</Link> }
    ]} />
    <Modal className="coverage-modal" open={Boolean(selected)} title={selected ? `${selected.title} - employees` : ''} onCancel={() => setSelectedKey('')} footer={null} width={1100} bodyStyle={{ maxHeight: '75vh', overflowY: 'auto' }} destroyOnClose>
      {selected && <div className="coverage-employee-dialog">
        <p>{selected.description}</p>
        <Segmented block value={filter} onChange={value => setFilter(value as Filter)} options={(['All', 'Uncovered', 'Review', 'Covered'] as const).map(value => ({ value, label: `${value} (${selected.results.filter(row => value === 'All' || row.status === value).length})` }))} />
        <DataTable key={`${selected.key}-${filter}`} rows={selected.results.filter(row => filter === 'All' || row.status === filter)} getRowId={row => row.employee.id} pageSizeOptions={[20, 50, 100]} fillHeight={false}
          exportFileName={`client-coverage-${selected.key}-${filter.toLowerCase()}-${asOf}`} columns={[
            { key: 'employeeCode', label: 'Employee code', value: row => row.employee.employeeCode, width: 140 },
            { key: 'employeeName', label: 'Employee', value: row => `${row.employee.firstName} ${row.employee.lastName}`.trim(), width: 200 },
            { key: 'department', label: 'Department', value: row => row.employee.department, width: 160 },
            { key: 'status', label: 'Status', width: 115, render: row => <Tag color={statusColor(row.status)}>{row.status}</Tag> },
            { key: 'detail', label: 'Policy / reason / next action', width: 400, wrap: true, filterable: false }
          ]} />
        <Link to={selected.route}>Open configuration</Link>
      </div>}
    </Modal>
  </section>
}
