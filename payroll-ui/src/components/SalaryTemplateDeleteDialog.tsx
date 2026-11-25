import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Modal, Pagination, Spin, Table } from 'antd'
import type { AuthUser, Structure } from '../types/payroll'
import { isSelectedClientUser } from '../utils/clientAccess'
import SalaryTemplatePayslipMappingEditor from './SalaryTemplatePayslipMappingEditor'
import {
  deleteClientSalaryTemplate, getEditableSalaryTemplateMappings, getSalaryTemplateDeletionPreview, unlinkSalaryTemplateMapping,
  type EditableSalaryTemplateMapping, type EditableSalaryTemplateMappingsPage, type SalaryTemplateDeletionPreview,
} from '../services/settingsService'

const pageSize = 10
const mappingKey = (item: EditableSalaryTemplateMapping) => JSON.stringify([item.kind, item.key])

function deletionGuidance(user: AuthUser): Record<string, string> {
  const has = (permission: string) => user.permissions.includes(permission)
  const employeeAccess = 'Removing an employee assignment requires Manage employees permission.'
  const excelAccess = !isSelectedClientUser(user)
  return {
    'Shared template': 'This template is shared. Keep it and use a client-specific template for future assignments.',
    'Employee Master': has('employees.manage')
      ? 'Current links for this client appear above. Links needed by saved payroll are retained. Recheck after queued or processing payroll finishes.'
      : employeeAccess,
    'Employee salary history': has('employees.manage')
      ? 'Only open, unprocessed Basic Pay assignments are editable. Closed, source-reviewed and payroll-used records retain their template. Recheck after pending payroll finishes.'
      : employeeAccess,
    'Location salary assignments': 'Location assignments retain the reviewed salary source. Review the employee and work location for future assignments.',
    'PF policy versions': has('tax.statutory.manage')
      ? 'A draft can be detached here and reassigned in Statutory Setup → PF Policy Versions. Published versions retain their template.'
      : 'Detaching a draft requires statutory management permission. Published PF versions retain their template.',
    'Managed formula input declarations': 'These declarations identify inputs required by saved salary formulas. Keep the template while the declarations reference it.',
    'Excel payslip header mappings': excelAccess && ['payroll.run', 'payroll.approve', 'payroll.payments'].some(has)
      ? 'Remove a header link above to clear the template selection for future imports.'
      : 'An administrator with payroll run, approval or payment access outside Selected Client mode can remove a header link.',
    'Saved Excel payslip batches': 'Saved batches retain their original template reference for previously issued payslips.',
    'Excel payroll setup records': excelAccess && ['settings.manage', 'employees.manage', 'payroll.run'].every(has)
      ? 'Draft template selections can be cleared here. Applied imports retain their original payroll setup.'
      : 'Unlinking a draft requires Settings, Manage employees and Run payroll access outside Selected Client mode. Applied imports retain their template.',
    'Saved payroll': 'These saved runs retain the template used to calculate payroll.',
    'Saved payroll calculation traces': 'These payroll calculation records retain the template used for the listed runs.',
    'Payslip templates requiring an active salary template': 'Create an active salary template with components for this client, then recheck the payslip field mappings.',
    'Payslip template field mappings': 'Review each design below to see which component fields need a replacement.',
  }
}

type Props = {
  clientId: number
  user: AuthUser
  template: Structure
  hasDraft: boolean
  onClose: () => void
  onDeleted: () => void
  onCreateReplacement: () => void
}

export default function SalaryTemplateDeleteDialog({ clientId, user, template, hasDraft, onClose, onDeleted, onCreateReplacement }: Props) {
  const [preview, setPreview] = useState<SalaryTemplateDeletionPreview | null>(null)
  const [editable, setEditable] = useState<EditableSalaryTemplateMappingsPage | null>(null)
  const [page, setPage] = useState(1)
  const [checked, setChecked] = useState<EditableSalaryTemplateMapping[]>([])
  const [review, setReview] = useState<EditableSalaryTemplateMapping[]>([])
  const [writing, setWriting] = useState(false)
  const [designWriting, setDesignWriting] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [readError, setReadError] = useState('')
  const [revision, setRevision] = useState(0)
  const writeInFlight = useRef(false)
  const mounted = useRef(true)
  useEffect(() => {
    mounted.current = true
    return () => { mounted.current = false }
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    void getSalaryTemplateDeletionPreview(clientId, template.id, controller.signal).then(result => {
      if (controller.signal.aborted) return
      const current = result.data
      if (result.ok && current?.templateId === template.id && current.version && Array.isArray(current.mappings)) {
        setPreview(current)
      } else {
        setReadError(result.error || 'Unable to check where this template is used.')
      }
    })
    return () => controller.abort()
  }, [clientId, template.id, revision])

  useEffect(() => {
    const controller = new AbortController()
    void getEditableSalaryTemplateMappings(clientId, template.id, (page - 1) * pageSize, pageSize, controller.signal).then(result => {
      if (controller.signal.aborted) return
      const data = result.data
      if (!result.ok || !data || !Number.isSafeInteger(data.total) || data.total < 0 || !Array.isArray(data.items)) {
        setReadError(result.error || 'Unable to load editable mappings.')
        return
      }
      const lastPage = Math.max(1, Math.ceil(data.total / pageSize))
      if (page > lastPage) setPage(lastPage)
      else setEditable(data)
    })
    return () => controller.abort()
  }, [clientId, template.id, revision, page])

  const checkAgain = (message = '') => {
    setError(message)
    setReadError('')
    setReview([])
    setChecked([])
    setEditable(null)
    setPreview(null)
    setRevision(value => value + 1)
  }
  const ready = !!preview && !!editable && !readError
  const busy = writing || designWriting
  const canDelete = ready && preview.mappings.length === 0 && editable.total === 0
  const guidance = deletionGuidance(user)
  const reviewRemoval = (items: EditableSalaryTemplateMapping[]) => {
    setError('')
    setNotice('')
    setReview(items)
  }
  const confirmAction = async () => {
    if (writeInFlight.current || designWriting || !ready || (!review.length && !canDelete)) return
    writeInFlight.current = true
    setWriting(true)
    setNotice('')
    try {
      if (!review.length) {
        const result = await deleteClientSalaryTemplate(clientId, template.id, preview.version)
        if (!mounted.current) return
        if (result.ok && result.status === 204) onDeleted()
        else checkAgain(result.error || 'Deletion could not be confirmed. Review the latest mappings before retrying.')
        return
      }
      let removed = 0
      for (const mapping of review) {
        const result = await unlinkSalaryTemplateMapping(clientId, template.id, mapping)
        if (!mounted.current) return
        if (!result.ok || result.status !== 204) {
          checkAgain(`${removed} of ${review.length} removals confirmed. Stopped at ${mapping.label}. ${result.error || 'Check the latest mappings before retrying.'}`)
          return
        }
        removed++
      }
      setNotice(review.length === 1 ? `Removed the mapping for ${review[0].label}.` : `Removed ${removed} mappings.`)
      checkAgain()
    } finally {
      writeInFlight.current = false
      if (mounted.current) setWriting(false)
    }
  }

  return <Modal open width={650} title={`Delete salary template · ${preview?.name || template.name}`} onCancel={() => { if (!busy) onClose() }}
    closable={!busy} maskClosable={!busy} keyboard={!busy} bodyStyle={{ maxHeight: '60vh', overflowY: 'auto' }}
    footer={<>
      <Button disabled={busy} onClick={review.length ? () => setReview([]) : onClose}>{review.length ? 'Back' : canDelete ? 'Cancel' : 'Close'}</Button>
      {!review.length && !canDelete && (readError || ready) && <Button disabled={busy} onClick={() => { setNotice(''); checkAgain() }}>Recheck mappings</Button>}
      {(review.length > 0 || canDelete) && <Button type="primary" danger loading={writing} onClick={() => void confirmAction()}>
        {review.length ? 'Confirm removal' : 'Delete template'}
      </Button>}
    </>}>
    {(readError || error) && <Alert showIcon type="error" message={readError || error} />}
    {notice && <Alert showIcon type="success" message={notice} />}
    {!ready && !readError && <p role="status"><Spin size="small" /> Checking template mappings…</p>}
    {ready && (review.length > 0 ? <Alert type="warning" showIcon message={`Remove ${review.length === 1 ? 'this mapping' : `these ${review.length} mappings`}?`}
      description={<ul>{review.map(item => <li key={mappingKey(item)}><strong>{item.type} · {item.label}</strong><br />{item.impact}</li>)}</ul>} /> : canDelete
      ? <p>Permanently delete this saved template?{hasDraft ? ' Your current edits will stay open as an unsaved draft.' : ''}</p>
      : <>
        {editable.total > 0 && <>
          <h4>Editable mappings ({editable.total})</h4>
          <Table<EditableSalaryTemplateMapping> size="small" dataSource={editable.items} rowKey={mappingKey} pagination={false}
            rowSelection={editable.items.length > 1 ? { selectedRowKeys: checked.map(mappingKey), getCheckboxProps: () => ({ disabled: busy }), onChange: (_, rows) => setChecked(rows) } : undefined}
            columns={[
              { title: 'Mapping', dataIndex: 'type' },
              { title: 'Record', dataIndex: 'label' },
              { title: 'Action', width: 92, render: (_, item) => <Button size="small" danger disabled={busy} onClick={() => reviewRemoval([item])}>Remove</Button> },
            ]} />
          {checked.length > 0 && <Button danger size="small" disabled={busy} onClick={() => reviewRemoval(checked)}>Remove selected ({checked.length})</Button>}
          <Pagination size="small" disabled={busy} hideOnSinglePage showSizeChanger={false} current={page} pageSize={pageSize} total={editable.total} onChange={next => { setChecked([]); setEditable(null); setPage(next) }} />
        </>}
        <h4>Links blocking deletion</h4>
        <ul>{preview.mappings.map(mapping => <li key={mapping.type}>
          <strong>{mapping.type} ({mapping.count})</strong>
          {mapping.examples.length > 0 && <div>{mapping.examples.join(' · ')}</div>}
          <div>{guidance[mapping.type] || 'Review this link with your payroll administrator.'}</div>
          {!!mapping.payslipDesigns?.length && <SalaryTemplatePayslipMappingEditor clientId={clientId} designs={mapping.payslipDesigns} disabled={writing} onBusyChange={setDesignWriting} onSaved={() => checkAgain()} />}
          {mapping.type === 'Payslip templates requiring an active salary template' && <Button size="small" disabled={busy} onClick={onCreateReplacement}>Create replacement salary template</Button>}
        </li>)}</ul>
      </>)}
  </Modal>
}
