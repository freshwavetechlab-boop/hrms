import { useState } from 'react'
import { CopyOutlined, DownloadOutlined, FileTextOutlined } from '@ant-design/icons'
import { Alert, Button, Col, Descriptions, Input, Modal, Row, Select, Space, Typography } from 'antd'
import examples from '../data/resumeTrainingExamples.json'
import './ResumeTrainingExamples.css'

// These deidentified reference cases are documentation only. Opening, copying
// or downloading an example never submits data or starts training.
export default function ResumeTrainingExamples() {
  const [open, setOpen] = useState(false)
  const [selected, setSelected] = useState(examples[0].id)
  const [copied, setCopied] = useState(false)
  const [copyError, setCopyError] = useState('')
  const item = examples.find(row => row.id === selected) ?? examples[0]
  const json = JSON.stringify(item.example, null, 2)
  const copy = async () => {
    setCopied(false); setCopyError('')
    try { await navigator.clipboard.writeText(json); setCopied(true) }
    catch {
      // Embedded browsers may not expose the async Clipboard API. Keep the
      // explicit user-click copy available through the browser's selection API.
      const previousFocus = document.activeElement
      const field = document.createElement('textarea')
      field.value = json; field.readOnly = true
      field.style.position = 'fixed'; field.style.left = '-9999px'
      document.body.append(field)
      try {
        field.select()
        if (!document.execCommand('copy')) throw new Error('Copy unavailable')
        setCopied(true)
      } catch { setCopyError('Copy is unavailable. Select the JSON below and copy it manually.') }
      finally { field.remove(); if (previousFocus instanceof HTMLElement) previousFocus.focus() }
    }
  }
  const download = () => {
    const jsonl = examples.map(row => JSON.stringify(row.example)).join('\n') + '\n'
    const url = URL.createObjectURL(new Blob([jsonl], { type: 'application/x-ndjson;charset=utf-8' }))
    const link = document.createElement('a')
    link.href = url; link.download = 'frevo-resume-training-examples.jsonl'
    link.click()
    window.setTimeout(() => URL.revokeObjectURL(url), 1000)
  }
  return <>
    <Button icon={<FileTextOutlined aria-hidden="true" />} onClick={() => setOpen(true)}>Training JSON examples</Button>
    <Modal title="Resume training JSON examples" open={open} onCancel={() => setOpen(false)}
      wrapClassName="resume-training-examples-modal"
      width={1040} style={{ maxWidth: 'calc(100vw - 24px)', top: 24 }}
      bodyStyle={{ maxHeight: 'calc(100dvh - 210px)', overflowY: 'auto', minWidth: 0 }}
      footer={<Space wrap>
        <Button onClick={() => setOpen(false)}>Close</Button>
        <Button icon={<CopyOutlined aria-hidden="true" />} onClick={() => void copy()}>{copied ? 'Copied' : 'Copy selected JSON'}</Button>
        <Button type="primary" icon={<DownloadOutlined aria-hidden="true" />} onClick={download}>Download examples (.jsonl)</Button>
      </Space>}
    >
      <Space direction="vertical" size={16} style={{ width: '100%', minWidth: 0 }}>
        <Alert type="info" showIcon message="Two checked reference examples"
          description="Use the same structure for your reviewed dataset. Replace sourceText and reviewedFields together. Keep only facts supported by the source; remove names, contact details, salary and personal identifiers." />
        <Select aria-label="Training example" value={selected} style={{ width: '100%' }}
          options={examples.map((row, index) => ({ value: row.id, label: `${index + 1}. ${row.title}` }))}
          onChange={value => { setSelected(value); setCopied(false); setCopyError('') }} />
        <Typography.Paragraph style={{ marginBottom: 0 }}>{item.explanation}</Typography.Paragraph>
        <Row gutter={[16, 16]} style={{ minWidth: 0, width: '100%', marginLeft: 0, marginRight: 0 }}>
          <Col xs={24} md={10} style={{ minWidth: 0 }}>
            <Typography.Text strong>Source evidence</Typography.Text>
            <Input.TextArea aria-label="Training example source evidence" readOnly value={item.example.sourceText}
              autoSize={{ minRows: 10, maxRows: 17 }} style={{ marginTop: 8 }} />
          </Col>
          <Col xs={24} md={14} style={{ minWidth: 0 }}>
            <Typography.Text strong>Training JSON — one dataset row</Typography.Text>
            <pre aria-label="Training JSON example" tabIndex={0} style={{ margin: '8px 0 0', padding: 12,
              border: '1px solid var(--ant-color-border, #d9d9d9)', borderRadius: 6,
              maxWidth: '100%', maxHeight: 420, overflow: 'auto', fontSize: 12 }}>{json}</pre>
          </Col>
        </Row>
        <Descriptions size="small" bordered column={1}>
          <Descriptions.Item label="JSONL file">One complete JSON object per line. The download contains both examples.</Descriptions.Item>
          <Descriptions.Item label="clientId">Use the actual client ID for your data. These synthetic reference examples use 0.</Descriptions.Item>
          <Descriptions.Item label="subjectRef">A stable SHA256 reference: 64 lowercase hexadecimal characters. Use a different reference for each new record and keep it unchanged for updates. Do not enter a name, phone or employee code.</Descriptions.Item>
          <Descriptions.Item label="verified / verificationKind">Set verified to true only after checking the labels. Use recruiter_review for manually reviewed real data, or verified_regression for a checked reference/test case.</Descriptions.Item>
          <Descriptions.Item label="verificationRef">Your review or passing-test reference, for example recruiter-review-001. Do not include personal data.</Descriptions.Item>
        </Descriptions>
        {copyError && <Alert type="error" showIcon message={copyError} />}
        <Typography.Text type="secondary">These buttons only copy/download examples. Dataset upload and training are separate actions; they are not started here.</Typography.Text>
      </Space>
    </Modal>
  </>
}
