import { useRef, useState } from 'react'
import { Alert, Button, Select, Space } from 'antd'
import { getPayslipDesign, savePayslipDesign, type SalaryTemplatePayslipDependency, type SavePayslipDesignRequest } from '../services/payslipService'
import { useAuthSession } from './AuthGate'

type Field = SalaryTemplatePayslipDependency['fields'][number]
type Selection = { design: SalaryTemplatePayslipDependency; field: Field; replacement?: string }
type Props = { clientId: number; designs: SalaryTemplatePayslipDependency[]; onSaved: () => void; disabled?: boolean; onBusyChange?: (busy: boolean) => void }

export default function SalaryTemplatePayslipMappingEditor({ clientId, designs, onSaved, disabled, onBusyChange }: Props) {
  const user = useAuthSession()?.user
  const canEdit = user?.isActive && !user.mustChangePassword && (user.permissions.includes('settings.manage') || user.roles.includes('super_admin'))
  const [selection, setSelection] = useState<Selection>()
  const [busy, setBusy] = useState(false)
  const [retryOnly, setRetryOnly] = useState(false)
  const [error, setError] = useState('')
  const pending = useRef<SavePayslipDesignRequest | null>(null)
  const inFlight = useRef(false)

  const apply = async () => {
    if (!canEdit || disabled || inFlight.current || !selection || selection.replacement === undefined || selection.design.hasApprovedPayroll) return
    const { design, field, replacement } = selection
    inFlight.current = true
    setBusy(true)
    onBusyChange?.(true)
    setError('')
    try {
      if (!pending.current) {
        const saved = await getPayslipDesign(clientId, design.templateId, { cache: 'no-store' })
        const profile = saved.data
        if (!saved.ok || !profile) throw new Error(saved.error || 'Unable to read the saved design.')
        const original = profile.fields.find(f => f.sourceColumn === field.sourceColumn)
        const monthly = original?.kind === 'info'
        if (profile.template.clientId !== clientId || profile.template.id !== design.templateId || profile.sourceKind !== 'CorePayroll' ||
            profile.profileHash !== design.profileHash || !original || (monthly ? original.infoToken !== 'COMPONENT_MONTHLY:' + field.code : original.nativeCode !== field.code))
          throw new Error('This mapping changed. Recheck mappings before editing it.')
        if (!replacement && profile.fields.length === 1) throw new Error('Choose a replacement. The design needs at least one field.')
        if (replacement && !field.replacements.includes(replacement)) throw new Error('Choose one of the available replacement codes.')
        const fields = replacement ? profile.fields.map(f => f !== original ? f : monthly
          ? { ...f, infoToken: 'COMPONENT_MONTHLY:' + replacement } : { ...f, nativeCode: replacement }) : profile.fields.filter(f => f !== original)
        pending.current = {
          requestId: crypto.randomUUID(), reviewed: true, template: profile.template, sourceKind: profile.sourceKind,
          sourceBatchId: profile.sourceBatchId, sourceHash: profile.sourceHash, existingProfileHash: profile.profileHash,
          simpleLayout: profile.simpleLayout, keepMappedZeroRows: profile.keepMappedZeroRows, amountDecimalPlaces: profile.amountDecimalPlaces,
          fields, protectApprovedPresentation: true,
        }
      }
      const result = await savePayslipDesign(pending.current)
      if (!result.ok || !result.data) {
        const uncertain = result.status === 0 || result.status >= 500
        setRetryOnly(uncertain)
        if (!uncertain) pending.current = null
        throw new Error(result.error || 'Save could not be confirmed. Recheck mappings or retry this change.')
      }
      pending.current = null
      setRetryOnly(false)
      setSelection(undefined)
      onSaved()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Unable to update this field mapping.')
    } finally {
      inFlight.current = false
      setBusy(false)
      onBusyChange?.(false)
    }
  }

  return <div>{designs.map(design => <div key={design.templateId}>
    <strong>{design.name}</strong>
    {design.hasApprovedPayroll
      ? <p>Approved payroll uses this design. Keep {design.fields.map(f => f.code).join(', ')} in another active salary template for this client.</p>
      : !canEdit ? <p>A payslip settings administrator can update {design.fields.map(f => f.code).join(', ')}.</p>
      : design.fields.map(field => {
        const editing = selection?.design.templateId === design.templateId && selection.field.sourceColumn === field.sourceColumn
        return <div key={field.sourceColumn}><Space wrap size="small">
          <span>{field.label} · {field.code}</span>
          {!design.canRemoveField && !field.replacements.length ? <span>Keep this component in a replacement salary template, or add another field to the payslip first.</span> : editing ? <>
            <Select aria-label={`Change ${design.name} field ${field.label}`} placeholder="Choose change" value={selection.replacement} disabled={busy || disabled || retryOnly} showSearch optionFilterProp="label" style={{ minWidth: 180 }}
              options={[...(design.canRemoveField ? [{ value: '', label: 'Remove this field' }] : []), ...field.replacements.map(code => ({ value: code, label: `Map to ${code}` }))]}
              onChange={replacement => { setError(''); setSelection({ design, field, replacement }) }} />
            <Button size="small" disabled={disabled || selection.replacement === undefined} loading={busy} onClick={() => void apply()}>{retryOnly ? 'Retry same change' : 'Save mapping change'}</Button>
            {!retryOnly && <Button size="small" disabled={busy || disabled} onClick={() => { setSelection(undefined); setError('') }}>Cancel</Button>}
          </> : <Button size="small" disabled={disabled || busy || retryOnly} onClick={() => { setSelection({ design, field }); setError('') }}>Edit mapping</Button>}
        </Space></div>
      })}
  </div>)}
    {error && <Alert type="error" showIcon message={error} />}
  </div>
}
