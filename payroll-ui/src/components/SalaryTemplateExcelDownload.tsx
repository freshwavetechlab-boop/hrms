import { useMemo, useState } from 'react'
import { Alert, Button, InputNumber, Modal, Select, Space, Tag } from 'antd'
import { DownloadOutlined } from '@ant-design/icons'
import type { Component, Structure, WorkLocation } from '../types/payroll'
import { scopeSalaryTemplates } from '../utils/salaryTemplateContext'
import { buildSalaryTemplateWorkbook } from '../utils/salaryTemplateWorkbook'
import { downloadXlsx } from '../utils/xlsx'
import './SalaryTemplateExcelExtractor.css'

type Props = { clientId: number; clientName: string; templates: Structure[]; components: Component[]; locations: WorkLocation[]; preferredTemplateId?: string; onClose: () => void }
const currentMonth = () => { const date = new Date(); return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}` }
const daysInMonth = (month: string) => /^\d{4}-(0[1-9]|1[0-2])$/.test(month) ? new Date(Number(month.slice(0, 4)), Number(month.slice(5)), 0).getDate() : 30

export default function SalaryTemplateExcelDownload({ clientId, clientName, templates, components, locations, preferredTemplateId, onClose }: Props) {
  const available = useMemo(() => scopeSalaryTemplates(templates, clientId).filter(template => template.active && template.lines.length > 0), [templates, clientId])
  const initial = available.find(template => String(template.id) === preferredTemplateId) || (available.length === 1 ? available[0] : undefined)
  const [templateId, setTemplateId] = useState(initial ? String(initial.id) : '')
  const [location, setLocation] = useState(initial?.excelSource?.workLocationLabel || '')
  const [month, setMonth] = useState(currentMonth)
  const [days, setDays] = useState<number | null>(initial?.excelSource?.payrollDays || daysInMonth(currentMonth()))
  const [error, setError] = useState(''), [downloaded, setDownloaded] = useState(false)
  const selected = available.find(template => String(template.id) === templateId)
  const eligible = available.filter(template => !template.excelSource?.workLocationLabel || template.excelSource.workLocationLabel === location)
  const locationOptions = [...new Set([...locations.filter(item => item.clientId === clientId && item.isActive).map(item => item.name), ...available.map(template => template.excelSource?.workLocationLabel || '').filter(Boolean)])].sort()
  const chooseTemplate = (value: string) => {
    const template = available.find(item => String(item.id) === value)
    setTemplateId(value); setError(''); setDownloaded(false)
    if (template?.excelSource?.workLocationLabel) setLocation(template.excelSource.workLocationLabel)
    setDays(template?.excelSource?.payrollDays || daysInMonth(month))
  }
  const download = () => {
    if (!selected || days == null) return
    try {
      const sheets = buildSalaryTemplateWorkbook(selected, components, { clientId, clientName, month, payrollDays: days, locationLabel: location })
      const name = `${clientName}-${selected.name}${location ? `-${location}` : ''}-${month}-formulas`.replace(/[<>:"/\\|?*\u0000-\u001f]/g, '-').slice(0, 180)
      downloadXlsx(`${name}.xlsx`, sheets); setDownloaded(true); setError('')
    } catch (failure) { setError(failure instanceof Error ? failure.message : 'Unable to download this template.') }
  }
  return <Modal open title="Download client template with formulas" width={720} className="salary-excel-extractor salary-template-excel-download" onCancel={onClose} footer={<Space wrap><Button onClick={onClose}>Close</Button><Button type="primary" icon={<DownloadOutlined />} disabled={!selected || days == null || !month} onClick={download}>Download Excel with formulas</Button></Space>}>
    <div className="salary-excel-client"><span>Client</span><strong>{clientName}</strong></div>
    {!available.length && <Alert type="info" showIcon message="No saved salary template for this client" description="Extract and save an existing salary sheet first. A new-client basic template is not included in this tool." />}
    {error && <Alert type="error" showIcon message={error} />}
    {downloaded && <Alert type="success" showIcon message="Formula workbook downloaded" description="Fill the highlighted inputs, add any required columns/formulas, then recalculate and save in Excel before extracting it here." />}
    <div className="salary-template-download-fields">
      <label className="salary-excel-field"><span>Location</span><Select aria-label="Download template location" showSearch optionFilterProp="label" value={location} options={[{ value: '', label: 'Client default' }, ...locationOptions.map(value => ({ value, label: value }))]} onChange={value => { setLocation(value); setError(''); setDownloaded(false); if (selected?.excelSource?.workLocationLabel && selected.excelSource.workLocationLabel !== value) setTemplateId('') }} /></label>
      <label className="salary-excel-field"><span>Saved salary template</span><Select aria-label="Download saved salary template" showSearch optionFilterProp="label" placeholder="Choose the client/location template" value={templateId || undefined} options={eligible.map(template => ({ value: String(template.id), label: template.name }))} onChange={chooseTemplate} /></label>
      <label className="salary-excel-field"><span>Pay month</span><input aria-label="Download template pay month" type="month" value={month} onChange={event => { setMonth(event.target.value); setDownloaded(false); if (!selected?.excelSource?.payrollDays) setDays(daysInMonth(event.target.value)) }} /></label>
      <label className="salary-excel-field"><span>Payroll divisor days</span><InputNumber aria-label="Download template payroll divisor" min={1} max={31} precision={0} value={days} onChange={value => { setDays(value); setDownloaded(false) }} /></label>
    </div>
    {selected && <p className="salary-excel-footnote"><Tag>{selected.lines.length} components</Tag>Saved formulas are exported with blank employee and attendance inputs. Downloading does not update the saved template or Employee Master.</p>}
  </Modal>
}
