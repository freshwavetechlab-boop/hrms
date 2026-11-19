import { useEffect, useState } from 'react'
import { Alert } from 'antd'
import { getJsonResult } from '../services/apiClient'

export default function AttendanceApprovalNotice({ clientId, month }: { clientId: number; month: string }) {
  const [status, setStatus] = useState<{ enabled: boolean; approved: number; total: number } | null>(null)
  useEffect(() => {
    let active = true
    setStatus(null)
    if (!clientId || !/^\d{4}-(0[1-9]|1[0-2])$/.test(month)) return
    void getJsonResult(`/api/leave-attendance/attendance/approval?clientId=${clientId}&month=${month}`, { enabled: false, rows: [] as { status: string }[] }, { toast: false, loader: false }).then(response => {
      if (active && response.ok) setStatus({ enabled: response.data.enabled, approved: response.data.rows.filter(row => row.status === 'Approved').length, total: response.data.rows.length })
    })
    return () => { active = false }
  }, [clientId, month])
  return status?.enabled ? <Alert style={{ marginBottom: 12 }} type="info" showIcon message="PI → Project Cell approval required" description={`Attendance saved here is working data. ${status.approved} of ${status.total} employees are finally approved for ${month}. Complete both stages in ESS/MSS → Attendance Review before payroll.`} /> : null
}
