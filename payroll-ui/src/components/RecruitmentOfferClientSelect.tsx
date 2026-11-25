import { useEffect, useMemo, useState, type CSSProperties } from 'react'
import { Select, Tag } from 'antd'
import { ClientOptionName, clientSearchText } from './ClientOption'
import { useAuthSession } from './AuthGate'
import { getRosterClientSummary, type RosterClientSummary } from '../services/recruitmentDocumentService'
import { rosterPermissions } from '../utils/recruitmentRosterOperations'
import { getRecruitmentClientConfigurationSummary, type RecruitmentClientConfigurationSummary } from '../services/recruitmentClientConfigurationService'
import './ExcelPayslipClientOption.css'
import './RecruitmentClientSelect.css'

// Reuse the Payslip client's saved-data colors for both the module picker and
// the shell's assigned-client picker; neither changes the user's client grants.
export default function RecruitmentOfferClientSelect({ options, value, onChange, loading, disabled, placeholder, style, allowClear = true, includeSetup = false, ...labels }: {
  options: { value: number; label: string; code?: string }[]; value?: number; onChange: (value?: number) => void
  loading?: boolean; disabled?: boolean; placeholder?: string; style?: CSSProperties; allowClear?: boolean
  includeSetup?: boolean
  'aria-label': string; 'data-testid'?: string
}) {
  const session = useAuthSession()
  const user = session?.originalUser || session?.user
  const access = rosterPermissions(user?.permissions || [])
  const canRead = access.canImport || access.canManageOffers || access.canRelease
    || Boolean(user?.permissions.some(permission => ['recruitment.proposal.manage', 'recruitment.proposal.approve'].includes(permission)))
  const scope = JSON.stringify([user?.id, user?.clientId, user?.permissions, user?.clientAccessMode, user?.assignedClientIds, user?.recruitmentScopeMode, user?.recruitmentLocationIds])
  const [snapshot, setSnapshot] = useState<{ scope: string; rows: RosterClientSummary[] }>({ scope: '', rows: [] })
  const [setupSnapshot, setSetupSnapshot] = useState<{ scope: string; rows: RecruitmentClientConfigurationSummary[] }>({ scope: '', rows: [] })
  const clientsWithData = useMemo(() => new Set([
    ...(snapshot.scope === scope ? snapshot.rows.filter(row => row.rosterCount > 0 || row.offerCount > 0).map(row => row.clientId) : []),
    ...(includeSetup && setupSnapshot.scope === scope ? setupSnapshot.rows.filter(row => row.hasConfiguration).map(row => row.clientId) : []),
  ]), [snapshot, setupSnapshot, scope, includeSetup])
  useEffect(() => {
    if (!canRead && !includeSetup) return
    let active = true
    let request: AbortController | undefined
    const refresh = async () => {
      request?.abort()
      const current = new AbortController()
      request = current
      await Promise.all([
        canRead ? getRosterClientSummary(current.signal).then(result => {
          if (active && !current.signal.aborted) setSnapshot({ scope, rows: result.ok ? result.data : [] })
        }) : Promise.resolve(),
        includeSetup ? getRecruitmentClientConfigurationSummary(current.signal).then(result => {
          if (active && !current.signal.aborted) setSetupSnapshot({ scope, rows: result.ok ? result.data : [] })
        }) : Promise.resolve(),
      ])
    }
    void refresh()
    const changed = () => { void refresh() }
    window.addEventListener('hrms:actions-changed', changed)
    window.addEventListener('recruitment:roster-changed', changed)
    window.addEventListener('focus', changed)
    return () => {
      active = false
      request?.abort()
      window.removeEventListener('hrms:actions-changed', changed)
      window.removeEventListener('recruitment:roster-changed', changed)
      window.removeEventListener('focus', changed)
    }
  }, [canRead, includeSetup, scope])
  return <Select {...labels} className={includeSetup && value && clientsWithData.has(value) ? 'recruitment-client-configured' : undefined} allowClear={allowClear} showSearch optionFilterProp="searchText" optionLabelProp="label" popupMatchSelectWidth={includeSetup ? false : undefined} dropdownStyle={includeSetup ? { minWidth: 'min(360px, calc(100vw - 24px))', maxWidth: 'calc(100vw - 24px)' } : undefined} value={value} placeholder={placeholder} loading={loading} disabled={disabled} style={style} onChange={onChange}>
    {options.map(option => <Select.Option key={option.value} value={option.value} label={option.label} searchText={clientSearchText(option.label, option.code)} className={clientsWithData.has(option.value) ? 'excel-payslip-client-has-batches' : 'excel-payslip-client-no-batches'}>
      <span className="excel-payslip-client-option-label"><ClientOptionName name={option.label} code={option.code} />{clientsWithData.has(option.value) && <Tag color="green">{includeSetup ? 'Recruitment setup / data' : 'Saved offers / employee lists'}</Tag>}</span>
    </Select.Option>)}
  </Select>
}
