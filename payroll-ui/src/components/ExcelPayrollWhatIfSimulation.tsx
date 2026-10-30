import { useState } from 'react'
import { Button } from 'antd'
import type { Component, Structure } from '../types/payroll'
import type { PayrollSetupPreview } from '../services/excelPayrollSetupService'
import PayrollSimulation from './PayrollSimulation'

export default function ExcelPayrollWhatIfSimulation({ clientId, row, disabled = false }: { clientId: number; row: PayrollSetupPreview['rows'][number]; disabled?: boolean }) {
  const [open, setOpen] = useState(false)
  const scenario = row.whatIfSimulation
  // Render only the server-resolved row snapshot, never a reconstructed Excel formula.
  // Older API versions expose no scenario; a fresh calculation preview is required.
  const valid = scenario?.mode === 'Template' && scenario.clientId === clientId && scenario.snapshot?.mode === 'Template'
    && scenario.snapshot.clientId === clientId && scenario.snapshot.components.length > 0
    && scenario.snapshot.components.every(c => /^\d+$/.test(c.id) && Number.isSafeInteger(Number(c.id)))
  const components: Component[] = valid ? scenario.snapshot.components.map(c => ({ ...c, id: Number(c.id), name: c.code, priority: String(c.priority), componentType: c.category, ctc: false, fbp: false, restrictFbp: false, epf: '', esi: false, recurring: true, scheduled: false, investmentType: '', correctionOf: '' })) : []
  const template: Structure = { id: 0, clientId: String(clientId), name: `Source row: ${row.employeeName || row.employeeCode}`, annualCtc: String(scenario?.annualCtc ?? ''), active: true, lines: valid ? scenario.snapshot.lines : [] }
  return <>
    <Button disabled={disabled || !valid} title={valid ? 'Test temporary salary and attendance inputs with these resolved rules' : 'Preview using the updated API to load the resolved calculation scenario'} onClick={() => setOpen(true)}>Try CTC & attendance</Button>
    {open && valid && <PayrollSimulation key={row.rowId} template={template} components={components} clients={[]} fixedClientId={clientId} sourceScenario={scenario} onClose={() => setOpen(false)} />}
  </>
}
