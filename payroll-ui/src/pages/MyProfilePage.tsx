import { Alert, Avatar, Card, Descriptions, Space, Typography } from 'antd'
import EmployeeAttachmentPanel from '../components/EmployeeAttachmentPanel'
import type { AuthUser } from '../types/payroll'

export default function MyProfilePage({ user }: { user: AuthUser }) {
  const initials = (user.displayName || user.email || 'User')
    .split(/\s+/)
    .map(part => part[0])
    .join('')
    .slice(0, 2)
    .toUpperCase()

  return <section style={{ display: 'grid', gap: 16 }}>
    <Card>
      <Space align="start" size={16} wrap>
        <Avatar size={64}>{initials}</Avatar>
        <div>
          <Typography.Title level={3} style={{ margin: 0 }}>{user.displayName}</Typography.Title>
          <Typography.Text type="secondary">{user.email}</Typography.Text>
        </div>
      </Space>
      <Descriptions bordered column={{ xs: 1, sm: 2 }} size="small" style={{ marginTop: 20 }}>
        <Descriptions.Item label="Role">{user.roles.join(', ') || 'Employee'}</Descriptions.Item>
        <Descriptions.Item label="Mobile">{user.mobile || '-'}</Descriptions.Item>
        <Descriptions.Item label="Client ID">{user.clientId || '-'}</Descriptions.Item>
        <Descriptions.Item label="Employee ID">{user.employeeId || '-'}</Descriptions.Item>
      </Descriptions>
    </Card>

    {user.clientId && user.employeeId
      ? <EmployeeAttachmentPanel clientId={user.clientId} employeeId={user.employeeId} selfService />
      : <Alert
          type="warning"
          showIcon
          message="Employee profile is not linked"
          description="Ask HR to link this login with an active employee and client before using employee documents."
        />}
  </section>
}
