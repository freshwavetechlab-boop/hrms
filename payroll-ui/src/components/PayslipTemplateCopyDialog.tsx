import { clientChoice, clientSelectProps } from './ClientOption'
import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Form, Input, Modal, Select, Space } from 'antd'
import { CopyOutlined } from '@ant-design/icons'
import type { Client, PayslipTemplate } from '../types/payroll'
import { getPayslipDesign, type SavedPayslipDesign } from '../services/payslipService'
import { excelPayslipTheme } from '../utils/payslipDesign'

type Props = {
  open: boolean
  templates: PayslipTemplate[]
  clients: Client[]
  targetClientId: number
  targetClientIds?: number[]
  fixedTargetClientId?: number
  initialSourceId?: number
  onClose: () => void
  onUse: (template: PayslipTemplate, seed: SavedPayslipDesign | null) => void
}
const clientIdOf = (template: PayslipTemplate) => Number(String(template.clientId).split(':')[0]) || 0

export default function PayslipTemplateCopyDialog(props: Props) {
  const { open, templates, clients, targetClientId, targetClientIds, fixedTargetClientId, initialSourceId, onClose, onUse } = props
  const allowedClients = new Map(clients.map(client => [client.id, client]))
  const sources = templates.filter(template => template.id > 0 && (allowedClients.has(clientIdOf(template)) || !template.clientId && template.theme !== excelPayslipTheme))
  const [sourceId, setSourceId] = useState<number>()
  const [targetId, setTargetId] = useState<number>()
  const [name, setName] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const sequence = useRef(0)
  const latest = useRef(props); latest.current = props
  const source = sources.find(template => template.id === sourceId)
  const targetReady = !!targetId && (!targetClientIds || targetClientIds.includes(targetId))

  useEffect(() => {
    sequence.current += 1
    setLoading(false); setError('')
    if (open) {
      const initial = sources.find(template => template.id === initialSourceId)
      setSourceId(initial?.id)
      setName(initial ? `${initial.name} copy` : '')
      const preferred = fixedTargetClientId || targetClientId
      setTargetId(clients.some(client => client.id === preferred) ? preferred : undefined)
    }
    return () => { sequence.current += 1 }
    // Reset when opening the picker; parent refreshes should not erase entered values.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open])

  const useTemplate = async () => {
    if (loading || !source || !targetId || !targetReady || !allowedClients.has(targetId) || !name.trim()) return
    if (fixedTargetClientId && targetId !== fixedTargetClientId) return
    const request = ++sequence.current
    setLoading(true); setError('')
    let seed: SavedPayslipDesign | null = null
    try {
      if (source.theme === excelPayslipTheme) {
        const response = await getPayslipDesign(clientIdOf(source), source.id)
        if (request !== sequence.current) return
        const saved = response.data
        if (!response.ok || !saved) throw new Error(response.error || 'The saved design could not be loaded. Retry after refreshing the template list.')
        const corePayroll = saved.sourceKind === 'CorePayroll'
        if (saved.template.id !== source.id || saved.template.clientId !== clientIdOf(source) || saved.template.theme !== excelPayslipTheme
          || saved.sourceKind != null && !['ExcelSource', 'CorePayroll'].includes(saved.sourceKind)
          || (corePayroll ? !!saved.sourceBatchId || typeof saved.simpleLayout !== 'boolean' || typeof saved.keepMappedZeroRows !== 'boolean' : !saved.sourceBatchId)
          || !/^[a-f0-9]{64}$/i.test(saved.sourceHash) || !/^[a-f0-9]{64}$/i.test(saved.profileHash) || !Array.isArray(saved.fields) || !saved.fields.length
          || ![0, 2].includes(saved.amountDecimalPlaces)) throw new Error('The saved design is incomplete or belongs to another template. Open its original setup and save it before copying.')
        seed = { ...saved, template: { ...saved.template }, fields: saved.fields.map(field => ({ ...field })), payPeriods: saved.payPeriods ? [...saved.payPeriods] : saved.payPeriods }
      }
      if (request !== sequence.current || !latest.current.open) return
      if (!latest.current.clients.some(client => client.id === targetId)
        || latest.current.targetClientIds && !latest.current.targetClientIds.includes(targetId)
        || !latest.current.templates.some(template => template.id === source.id && clientIdOf(template) === clientIdOf(source))
        || latest.current.fixedTargetClientId && latest.current.fixedTargetClientId !== targetId) throw new Error('Available clients or templates changed. Choose them again before creating a copy.')
      onUse({ ...(seed?.template ?? source), id: 0, clientId: String(targetId), name: name.trim() }, seed)
    } catch (caught) {
      if (request === sequence.current) setError(caught instanceof Error ? caught.message : 'Unable to prepare this template copy.')
    } finally {
      if (request === sequence.current) setLoading(false)
    }
  }

  return <Modal open={open} title="Create similar payslip template" width={620} onCancel={loading ? undefined : onClose} closable={!loading} maskClosable={!loading} footer={<Space wrap><Button disabled={loading} onClick={onClose}>Cancel</Button><Button type="primary" icon={<CopyOutlined />} loading={loading} disabled={!source || !targetReady || !targetId || !allowedClients.has(targetId) || !name.trim()} onClick={() => void useTemplate()}>Use template</Button></Space>}>
    <Form component={false} layout="vertical">
      <Form.Item label="Copy from client / template" required><Select aria-label="Source payslip template" disabled={loading} value={sourceId} placeholder="Search client or template" {...clientSelectProps(sources.map(template => ({ value: template.id, code: allowedClients.get(clientIdOf(template))?.code, label: `${allowedClients.get(clientIdOf(template))?.name || 'All clients'} · ${template.name} · ${template.theme}` })))} onChange={id => { const selected = sources.find(template => template.id === id); setSourceId(id); setName(selected ? `${selected.name} copy` : ''); setError('') }} /></Form.Item>
      <Form.Item label="Create for client" required><Select aria-label="Target payslip client" disabled={loading || !!fixedTargetClientId} value={targetId} placeholder="Select client" {...clientSelectProps(clients.map(client => ({ ...clientChoice(client), disabled: !!targetClientIds && !targetClientIds.includes(client.id) })))} onChange={id => { setTargetId(id); setError('') }} /></Form.Item>
      <Form.Item label="New template name" required><Input aria-label="Copied payslip template name" disabled={loading} value={name} onChange={event => setName(event.target.value)} onPressEnter={event => { event.preventDefault(); void useTemplate() }} /></Form.Item>
    </Form>
    <p>Copy the design and field mappings into an editable draft, then change it or save it as it is. No Excel upload is required for the new client. Amounts and employee details come from that client’s core payroll.</p>
    {!sources.length && <Alert type="info" showIcon message="No saved templates are available to copy." />}
    {error && <Alert type="error" showIcon message={error} />}
  </Modal>
}
