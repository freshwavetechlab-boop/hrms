import { useState } from 'react'
import { Button, Checkbox, Collapse, Input, Select, Tag } from 'antd'
import { ArrowDownOutlined, ArrowUpOutlined, DeleteOutlined, PlusOutlined } from '@ant-design/icons'
import type { PayslipDesignCatalog, PayslipDesignField, PayslipDesignKind } from '../services/payslipService'
import { matchingPayslipComponent, nativePayslipKind, payslipDesignInformationLabel, payslipDesignInformationOptions } from '../utils/payslipDesign'
import SearchSelect from './SearchSelect'
import './PayslipFieldPicker.css'

type Props = {
  catalog: PayslipDesignCatalog
  fields: PayslipDesignField[]
  disabled: boolean
  portable: boolean
  copyReview?: { sourceName: string; targetName: string; salaryTemplateNames: Map<string, string[]> }
  onChange: (fields: PayslipDesignField[]) => void
}
const groups: { kind: PayslipDesignKind; title: string }[] = [
  { kind: 'info', title: 'Employee details' },
  { kind: 'earning', title: 'Earnings' },
  { kind: 'deduction', title: 'Deductions' },
  { kind: 'employer', title: 'Employer contributions / Benefits' },
]
const MAX_FIELDS = 100

export default function PayslipFieldPicker({ catalog, fields, disabled, portable, copyReview, onChange }: Props) {
  const [salarySelection, setSalarySelection] = useState<string[]>([])
  const [employeeSelection, setEmployeeSelection] = useState<string[]>([])
  const availableSalary = catalog.nativeCodes.flatMap(component => {
    const kind = nativePayslipKind(component.category)
    if (!kind || fields.some(field => field.kind === kind && field.nativeCode === component.code)) return []
    return [{ key: `${kind}:${component.code}`, component, kind }]
  }).filter((choice, index, all) => all.findIndex(other => other.key === choice.key) === index)
  const availableInformation = [...new Set(catalog.infoTokens)].filter(token => !fields.some(field => field.kind === 'info' && field.infoToken === token))
  const selectedSalary = salarySelection.filter(key => availableSalary.some(choice => choice.key === key))
  const selectedInformation = employeeSelection.filter(token => availableInformation.includes(token))
  const copiedFields = new Map(copyReview ? catalog.fields.map(field => [field.sourceColumn, field]) : [])
  const unusedTemplateFields = availableSalary.filter(choice => copyReview?.salaryTemplateNames.has(choice.key))
  const remaining = Math.max(0, MAX_FIELDS - fields.length)
  const patch = (column: number, value: Partial<PayslipDesignField>) => {
    if (!disabled) onChange(fields.map(field => field.sourceColumn === column ? { ...field, ...value } : field))
  }
  const add = (type: 'salary' | 'employee') => {
    if (disabled || !portable) return
    const selected = type === 'salary' ? selectedSalary : selectedInformation
    if (!selected.length || selected.length > remaining) return
    const used = new Set(fields.map(field => field.sourceColumn))
    // A new row must not inherit the provenance of a removed source row.
    for (const column of copiedFields.keys()) used.add(column)
    const additions: PayslipDesignField[] = []
    for (const key of selected) {
      let sourceColumn = 0
      while (sourceColumn < 1500 && used.has(sourceColumn)) sourceColumn++
      if (sourceColumn >= 1500) return
      used.add(sourceColumn)
      if (type === 'salary') {
        const choice = availableSalary.find(item => item.key === key)
        if (!choice) return
        additions.push({ sourceColumn, label: choice.component.name || choice.component.code, kind: choice.kind, nativeCode: choice.component.code, infoToken: '', visible: true })
      } else additions.push({ sourceColumn, label: payslipDesignInformationLabel(key, catalog.nativeCodes), kind: 'info', nativeCode: '', infoToken: key, visible: true })
    }
    onChange([...fields, ...additions])
    if (type === 'salary') setSalarySelection([]); else setEmployeeSelection([])
  }
  const move = (column: number, direction: -1 | 1) => {
    if (disabled) return
    const index = fields.findIndex(field => field.sourceColumn === column)
    if (index < 0) return
    const peers = fields.map((field, position) => field.kind === fields[index].kind ? position : -1).filter(position => position >= 0)
    const other = peers[peers.indexOf(index) + direction]
    if (other == null) return
    const next = [...fields]
    ;[next[index], next[other]] = [next[other], next[index]]
    onChange(next)
  }
  return <section className="payslip-field-picker" aria-label="Payslip fields">
    {portable && <div className="payslip-field-picker-additions">
      <div className="payslip-field-picker-add-control"><label htmlFor="payslip-add-salary-fields">Add salary fields</label><div><Select id="payslip-add-salary-fields" aria-label="Add salary fields" mode="multiple" showSearch optionFilterProp="label" disabled={disabled || !remaining} value={selectedSalary} maxTagCount="responsive" placeholder="Select salary components" options={groups.filter(group => group.kind !== 'info').map(group => ({ label: group.title, options: availableSalary.filter(choice => choice.kind === group.kind).map(choice => ({ value: choice.key, label: `${choice.component.name} · ${choice.component.code}` })) }))} onChange={setSalarySelection} /><Button icon={<PlusOutlined />} disabled={disabled || !selectedSalary.length || selectedSalary.length > remaining} onClick={() => add('salary')}>Add</Button></div></div>
      <div className="payslip-field-picker-add-control"><label htmlFor="payslip-add-employee-fields">Add employee fields</label><div><Select id="payslip-add-employee-fields" aria-label="Add employee fields" mode="multiple" showSearch optionFilterProp="label" disabled={disabled || !remaining} value={selectedInformation} maxTagCount="responsive" placeholder="Select employee details" options={availableInformation.map(token => ({ value: token, label: payslipDesignInformationLabel(token, catalog.nativeCodes) }))} onChange={setEmployeeSelection} /><Button icon={<PlusOutlined />} disabled={disabled || !selectedInformation.length || selectedInformation.length > remaining} onClick={() => add('employee')}>Add</Button></div></div>
      {(selectedSalary.length > remaining || selectedInformation.length > remaining || !remaining) && <small className="payslip-field-picker-limit" role="status">{remaining ? `You can add ${remaining} more ${remaining === 1 ? 'field' : 'fields'}.` : 'Maximum 100 fields selected. Remove a field to add another.'}</small>}
    </div>}
    {copyReview && unusedTemplateFields.length > 0 && <details className="payslip-copy-available">
      <summary>{unusedTemplateFields.length} salary template {unusedTemplateFields.length === 1 ? 'component available' : 'components available'} to add</summary>
      <p>Select these under <strong>Add salary fields</strong> to include them in this payslip.</p>
      <ul>{unusedTemplateFields.map(({ key, component }) => <li key={key}><strong>{component.name} · {component.code}</strong> — {copyReview.salaryTemplateNames.get(key)?.join(', ')}</li>)}</ul>
    </details>}
    <Collapse defaultActiveKey={groups.map(group => group.kind)} size="small" className="payslip-field-picker-groups">
      {groups.map(group => {
        const grouped = fields.filter(field => field.kind === group.kind)
        return <Collapse.Panel key={group.kind} header={<span className="payslip-field-picker-group-title"><strong>{group.title}</strong><Tag>{grouped.filter(field => field.visible).length} / {grouped.length} shown</Tag></span>}>
          {grouped.length ? <div className="payslip-field-picker-list">{grouped.map((field, position) => {
            const original = copiedFields.get(field.sourceColumn)
            const originalCode = original?.nativeCode || original?.infoToken
            const salaryField = field.kind !== 'info'
            const match = copyReview && salaryField ? matchingPayslipComponent(catalog.nativeCodes, field) : undefined
            const needsMapping = !!copyReview && salaryField && !match
            const templateNames = match && copyReview?.salaryTemplateNames.get(`${field.kind}:${match.code}`)
            const componentOptions = salaryField ? catalog.nativeCodes.filter(code => nativePayslipKind(code.category) === field.kind
              && (code.code === field.nativeCode || !fields.some(other => other.sourceColumn !== field.sourceColumn && other.kind === field.kind && other.nativeCode === code.code)))
              .map(code => ({ value: code.code, label: `${code.name} · ${code.code}` })) : []
            return <div className={`payslip-field-picker-row${needsMapping ? ' payslip-copy-unmatched' : ''}`} key={field.sourceColumn}>
            <div className="payslip-field-picker-label">{portable && field.sourceColumn < 1500 ? <Input size="small" aria-label={`${field.label} display label`} value={field.label} disabled={disabled} maxLength={150} onChange={event => patch(field.sourceColumn, { label: event.target.value })} /> : <strong>{field.label}</strong>}{field.sourceColumn >= 1500 && <small>Master information</small>}
              {copyReview && <Tag className="payslip-copy-tag" title={original ? `${copyReview.sourceName} · ${original.label}` : undefined}>{original ? ['Copied', originalCode].filter(Boolean).join(' · ') : 'Added here'}</Tag>}
            </div>
            <div className="payslip-field-picker-mapping">
              <label><span className="payslip-field-picker-sr-only">{field.label} mapping</span><SearchSelect disabled={disabled || field.sourceColumn >= 1500} value={salaryField ? (needsMapping ? undefined : field.nativeCode) : field.infoToken} allowClear={!salaryField && !field.visible} placeholder={salaryField ? 'Select salary component' : 'Select employee field'} options={salaryField ? componentOptions : payslipDesignInformationOptions(catalog, field.infoToken)} onChange={value => patch(field.sourceColumn, salaryField ? { nativeCode: value, infoToken: '' } : { infoToken: value, nativeCode: '' })} /></label>
              {copyReview && salaryField && <Tag className="payslip-copy-tag" color={needsMapping ? 'warning' : templateNames?.length ? 'green' : 'blue'}>
                {needsMapping ? 'Choose a matching component or remove this row.' : templateNames?.length ? `Salary template · ${templateNames.join(', ')}` : 'Available in client payroll'}
              </Tag>}
            </div>
            <div className="payslip-field-picker-actions"><Checkbox checked={field.visible} disabled={disabled} onChange={event => patch(field.sourceColumn, { visible: event.target.checked })}>Show</Checkbox><Button type="text" size="small" icon={<ArrowUpOutlined />} aria-label={`Move ${field.label} up within ${group.title}`} disabled={disabled || position === 0} onClick={() => move(field.sourceColumn, -1)} /><Button type="text" size="small" icon={<ArrowDownOutlined />} aria-label={`Move ${field.label} down within ${group.title}`} disabled={disabled || position === grouped.length - 1} onClick={() => move(field.sourceColumn, 1)} />{portable && <Button type="text" size="small" danger icon={<DeleteOutlined />} aria-label={`Remove ${field.label} from this payslip design`} disabled={disabled || fields.length <= 1} onClick={() => { if (!disabled && fields.length > 1) onChange(fields.filter(item => item.sourceColumn !== field.sourceColumn)) }} />}</div>
          </div>})}</div> : <p className="payslip-field-picker-empty">No {group.title.toLowerCase()} selected.</p>}
        </Collapse.Panel>
      })}
    </Collapse>
  </section>
}
