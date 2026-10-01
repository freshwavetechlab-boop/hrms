import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { Alert, Button } from 'antd'
import DataTable from './DataTable'
import { useAuthSession } from './AuthGate'
import { getAttendanceGaps, type AttendanceConfigurationGap } from '../services/attendanceIntegrationService'

export default function AttendanceConfigurationGaps({ clientId }: { clientId: number }) {
  const [rows, setRows] = useState<AttendanceConfigurationGap[]>([])
  const [error, setError] = useState('')
  const navigate = useNavigate()
  const session = useAuthSession()
  useEffect(() => {
    let active = true
    let attempt = 0
    const load = async () => { const current = ++attempt; const result = await getAttendanceGaps(clientId); if (active && current === attempt) { setRows(result.data.filter(row => row.requiredPermissions?.some(permission => session?.user.permissions.includes(permission)))); setError(result.error) } }
    void load()
    window.addEventListener('hrms:actions-changed', load)
    return () => { active = false; window.removeEventListener('hrms:actions-changed', load) }
  }, [clientId, session?.user.permissions.join('|')])
  return error ? <Alert type="error" message={error} /> : rows.length ? <details className="employee-missing-information"><summary>Missing configuration ({rows.length})</summary><DataTable rows={rows.map((row, id) => ({ ...row, id }))} columns={[{ key: 'title', label: 'Setting' }, { key: 'detail', label: 'Status' }]} actions={row => <Button size="small" onClick={() => navigate(row.route)}>Configure</Button>} exportFileName="missing-attendance-configuration" /></details> : null
}
