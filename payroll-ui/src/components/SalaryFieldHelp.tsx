import { useState } from 'react'
import { Button, Input, Popover } from 'antd'
import { QuestionCircleOutlined } from '@ant-design/icons'
import type { SalaryHelpOption } from '../utils/salaryFieldGuides'
import './SalaryComponentEditor.css'

export default function SalaryFieldHelp({ label, text, example, options = [] }: { label: string; text: string; example: string; options?: SalaryHelpOption[] }) {
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const visibleOptions = options.filter(option => `${option.label} ${option.meaning}`.toLowerCase().includes(query.toLowerCase()))
  return <Popover open={open} onOpenChange={next => { setOpen(next); if (next) setQuery('') }} trigger="click" placement="top" zIndex={2200} title={label}
    content={<div className="salary-field-help-content">{options.length > 8 && <Input size="small" aria-label="Find a help option" placeholder="Find an option" allowClear value={query} onChange={event => setQuery(event.target.value)} />}<div className="salary-field-help-scroll" tabIndex={0}><p>{text}</p><p><strong>Example</strong><br />{example}</p>{options.length > 0 && <dl className="salary-field-help-options">{visibleOptions.map(option => <div key={option.label}><dt>{option.label}</dt><dd>{option.meaning}<p><strong>Example: </strong>{option.example}</p></dd></div>)}</dl>}{options.length > 0 && !visibleOptions.length && <p>No matching option.</p>}</div><Button size="small" onClick={() => setOpen(false)}>Got it</Button></div>}>
    <Button type="text" size="small" className="salary-field-help-button" icon={<QuestionCircleOutlined />} aria-label={`Help: ${label}`} aria-expanded={open} onKeyDown={event => { if (event.key === 'Escape') setOpen(false) }} />
  </Popover>
}
