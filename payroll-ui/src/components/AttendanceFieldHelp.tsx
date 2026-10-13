import { Button, Popover } from 'antd'
import { QuestionCircleOutlined } from '@ant-design/icons'

export default function AttendanceFieldHelp({ label, text, example }: { label: string; text: string; example: string }) {
  return <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6 }}>{label}<Popover trigger="click" placement="top" zIndex={2200} title={label} content={<div style={{ maxWidth: 320, lineHeight: 1.6 }}><p>{text}</p><p><strong>Example: </strong>{example}</p></div>}><Button type="text" size="small" aria-label={`Help: ${label}`} icon={<QuestionCircleOutlined />} /></Popover></span>
}
