import type { ReactNode } from 'react'
import { Checkbox, Input, InputNumber } from 'antd'
import './SmartBulkUploadMapper.css'

export default function EmployeeCodeGeneration(p: { enabled: boolean; digits: number; clientCode: string; nextCode: string; disabled?: boolean; generationDisabled?: boolean; testIdPrefix?: string; onEnabled: (value: boolean) => void; onDigits: (value: number) => void; children?: ReactNode }) {
  const id = p.testIdPrefix || 'smart-bulk'
  return <section className="smart-bulk-smart-tools single" data-testid={`${id}-smart-tools`}>
    <article className={p.enabled ? 'active' : ''}>
      <header><div><b>Missing Employee Code</b><span>Existing spreadsheet codes are preserved. Only blank codes are generated.</span></div><Checkbox data-testid={`${id}-generate-codes`} disabled={p.disabled || p.generationDisabled || !p.clientCode} checked={p.enabled} onChange={event => p.onEnabled(event.target.checked)}>Generate blanks</Checkbox></header>
      <div className="smart-bulk-tool-grid"><label><span>Client prefix</span><Input aria-label={`${id} client prefix`} value={p.clientCode.toUpperCase()} readOnly placeholder="Select a client" /></label><label><span>Sequence digits</span><InputNumber aria-label={`${id} sequence digits`} data-testid={`${id}-code-digits`} disabled={p.disabled || p.generationDisabled} min={1} max={12} precision={0} value={p.digits} onChange={value => p.onDigits(Number(value || 1))} /></label><label><span>Next available</span><Input aria-label={`${id} next employee code`} data-testid={`${id}-next-code`} value={p.nextCode} readOnly /></label></div>
      {p.children}
    </article>
  </section>
}
