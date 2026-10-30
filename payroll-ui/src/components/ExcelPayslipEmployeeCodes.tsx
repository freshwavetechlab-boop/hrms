import { useEffect, useState } from 'react'
import { Alert, Button } from 'antd'
import EmployeeCodeGeneration from './EmployeeCodeGeneration'
import { getPayslipEmployeeCodeOptions, validPayslipCodeOptions, type PayslipEmployeeCodeGeneration, type PayslipEmployeeCodeOptions } from '../services/excelPayslipService'
import { nextEmployeeCodePreview } from '../utils/smartBulkImport'

export default function ExcelPayslipEmployeeCodes(p: { clientId: number; blankCount: number; providedCodes: string[]; value?: PayslipEmployeeCodeGeneration; disabled: boolean; onChange: (value: PayslipEmployeeCodeGeneration) => void }) {
  const [state, setState] = useState<{ clientId: number; data: PayslipEmployeeCodeOptions | null; error: string }>({ clientId: 0, data: null, error: '' })
  const [retry, setRetry] = useState(0)
  useEffect(() => {
    let active = true
    void getPayslipEmployeeCodeOptions(p.clientId).then(result => {
      if (active) setState({ clientId: p.clientId, data: result.ok && validPayslipCodeOptions(result.data) ? result.data : null, error: result.ok && validPayslipCodeOptions(result.data) ? '' : 'Employee-code options need the updated API. Existing Excel codes are kept.' })
    })
    return () => { active = false }
  }, [p.clientId, retry])
  const options = state.clientId === p.clientId ? state.data : null
  const value = p.value ?? { enabled: true, digits: options?.digits ?? 5 }
  return <div className="excel-payslip-code-options" aria-label="Payslip employee code generation">
    <EmployeeCodeGeneration testIdPrefix="payslip" enabled={value.enabled} digits={value.digits} clientCode={options?.clientCode || ''} nextCode={options ? nextEmployeeCodePreview(options.clientCode, value.digits, p.providedCodes, options.nextSequence) : ''} disabled={p.disabled || !options} onEnabled={enabled => p.onChange({ ...value, enabled })} onDigits={digits => p.onChange({ ...value, digits })}>
      <small>{p.blankCount} blank source codes. Verified Master matches reuse their existing code. New codes are allocated on Save after checking the latest counter.</small>
    </EmployeeCodeGeneration>
    {state.clientId === p.clientId && state.error && <Alert type="info" message={state.error} action={<Button size="small" disabled={p.disabled} onClick={() => setRetry(v => v + 1)}>Retry code options</Button>} />}
  </div>
}
