import { useState } from 'react'
import { Form, Input, Modal, Typography, message } from 'antd'
import SearchSelect, { selectOptions } from './SearchSelect'
import { getDropdowns, saveDropdown } from '../services/settingsService'
import type { Drop } from '../types/payroll'

type Props = {
  masterType: string
  clientId: number
  clientName?: string
  value: string
  values: string[]
  dropdowns: Drop[]
  onChange: (value: string) => void
  onDropdownsChange: (rows: Drop[]) => void
  emptyLabel?: string
  disabled?: boolean
  testId?: string
}

export default function RecruitmentMasterSelect({ masterType, clientId, clientName, value, values, dropdowns, onChange, onDropdownsChange, emptyLabel, disabled = false, testId }: Props) {
  const [open, setOpen] = useState(false)
  const [draft, setDraft] = useState('')
  const [saving, setSaving] = useState(false)
  const inputId = `recruitment-master-${masterType.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`
  const addDisabled = disabled || clientId <= 0
  const close = () => { setOpen(false); setDraft('') }
  const startAdd = () => {
    if (addDisabled) return
    setDraft('')
    setOpen(true)
  }
  const save = async () => {
    const nextValue = draft.trim()
    if (!nextValue) return message.warning(`Enter ${masterType.toLowerCase()}.`)
    const sameType = dropdowns.filter(row => row.type.localeCompare(masterType, undefined, { sensitivity: 'accent' }) === 0)
    const sameValue = (row: Drop) => row.value.trim().localeCompare(nextValue, undefined, { sensitivity: 'accent' }) === 0
    const activeExisting = sameType.find(row => sameValue(row) && row.isActive && (Number(row.clientId || 0) === 0 || Number(row.clientId || 0) === clientId))
    if (activeExisting) {
      onChange(activeExisting.value)
      message.info(`${activeExisting.value} already exists and has been selected.`)
      close()
      return
    }
    const inactiveClientValue = sameType.find(row => sameValue(row) && !row.isActive && Number(row.clientId || 0) === clientId)
    const payload: Drop = inactiveClientValue
      ? { ...inactiveClientValue, value: nextValue, isActive: true }
      : { id: 0, clientId, type: masterType, value: nextValue, configJson: '', isActive: true }
    setSaving(true)
    try {
      const response = await saveDropdown(payload, { toast: false })
      if (!response.ok) return message.error(response.error || `Unable to add ${masterType.toLowerCase()}.`)
      const refreshed = await getDropdowns()
      onDropdownsChange(refreshed)
      onChange(nextValue)
      message.success(`${masterType} added for ${clientName || 'the selected client'}.`)
      close()
    } finally {
      setSaving(false)
    }
  }

  return <>
    <SearchSelect
      value={value}
      onChange={onChange}
      options={selectOptions(values, emptyLabel)}
      disabled={disabled}
      testId={testId}
      addAction={{
        label: `Add ${masterType}`,
        onClick: startAdd,
        disabled: addDisabled,
        disabledReason: clientId <= 0 ? 'Select a client first.' : undefined,
        testId: testId ? `${testId}-add` : undefined,
      }}
    />
    <Modal
      title={`Add ${masterType}`}
      open={open}
      okText="Add value"
      confirmLoading={saving}
      okButtonProps={{ disabled: !draft.trim() }}
      onCancel={close}
      onOk={() => void save()}
      destroyOnClose
    >
      <Typography.Paragraph type="secondary">This value will be available to {clientName || 'the selected client'} wherever {masterType} is used.</Typography.Paragraph>
      <Form layout="vertical">
        <Form.Item label={masterType} htmlFor={inputId} required>
          <Input id={inputId} autoFocus value={draft} onChange={event => setDraft(event.target.value)} onPressEnter={() => draft.trim() && void save()} />
        </Form.Item>
      </Form>
    </Modal>
  </>
}
