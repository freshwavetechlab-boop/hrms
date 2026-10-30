import { useEffect, useState, type ReactNode } from 'react'
import { Button, Checkbox, Space, Tag, Tooltip } from 'antd'
import { previewPayslipEmployeeCodes, previewPayslipEmployeeIdentities, type PayslipCodeMatch, type PayslipIdentityInput, type PayslipIdentityMatch } from '../services/excelPayslipService'
import { PayslipCodePreviewContext, usePayslipCodePreview } from '../hooks/usePayslipCodePreview'
import { normalizePayslipCode, validPayslipCodePreview, validPayslipIdentityPreview } from '../utils/excelPayslipCodePreview'

const previewTooltipContainer=(trigger:HTMLElement)=>trigger.closest<HTMLElement>('.ant-modal-wrap, .ant-drawer-content-wrapper')||document.body

export default function ExcelPayslipCodePreview({ clientId, codes, identityRows, children }: { clientId: number; codes: string[]; identityRows?:PayslipIdentityInput[]; children: ReactNode }) {
  const [state, setState] = useState<{ key: string; matches: PayslipCodeMatch[];identities?:PayslipIdentityMatch[]; status: 'Ready' | 'Error' }>({ key: '', matches: [], status: 'Ready' })
  const [existingFirst, setExistingFirst] = useState(true), [retry, setRetry] = useState(0)
  const requestKey = JSON.stringify({ clientId, codes: [...new Set(codes.map(normalizePayslipCode).filter(Boolean))].sort(),identityRows })
  useEffect(() => {
    const request: { clientId: number; codes: string[];identityRows?:PayslipIdentityInput[] } = JSON.parse(requestKey)
    if (!(request.identityRows?.length||request.codes.length)) return
    let active = true
    const controller = new AbortController()
    if(request.identityRows)void previewPayslipEmployeeIdentities(request.clientId,request.identityRows,controller.signal).then(result=>{
      const valid=result.ok&&validPayslipIdentityPreview(result.data,request.clientId,request.identityRows!)
      if(active)setState({key:requestKey,matches:[],identities:valid?result.data!.rows:[],status:valid?'Ready':'Error'})
    })
    else void previewPayslipEmployeeCodes(request.clientId, request.codes, controller.signal).then(result => {
      if (active) setState({ key: requestKey, matches: result.ok && validPayslipCodePreview(result.data, request.codes) ? result.data!.matches : [], status: result.ok && validPayslipCodePreview(result.data, request.codes) ? 'Ready' : 'Error' })
    })
    return () => { active = false; controller.abort() }
  }, [requestKey, retry])
  const status = !(identityRows?identityRows.length:codes.some(code => code.trim())) ? 'Ready' : state.key === requestKey ? state.status : 'Checking'
  const matches = new Map(status === 'Ready' && state.key === requestKey ? state.matches.map(match => [normalizePayslipCode(match.code), match]) : [])
  const identities=new Map(status==='Ready'&&state.key===requestKey?state.identities?.map(row=>[row.sourceRow,row]):[])
  return <PayslipCodePreviewContext.Provider value={{ enabled: true, status, matches,identities,identityMode:!!identityRows, existingFirst, setExistingFirst, retry: () => setRetry(v => v + 1), orderKey: `${existingFirst}:${status}:${retry}:${state.key===requestKey}` }}>{children}</PayslipCodePreviewContext.Provider>
}

export function PayslipCodePreviewTools({ compact = false }: { compact?: boolean }) {
  const preview = usePayslipCodePreview()
  if (!preview.enabled) return null
  return <Space wrap className={`excel-payslip-code-preview-tools${compact ? ' excel-payslip-code-preview-tools--compact' : ''}`} aria-label="Employee code preview controls">
    <Checkbox checked={preview.existingFirst} disabled={preview.status !== 'Ready'} onChange={event => preview.setExistingFirst(event.target.checked)}>Existing codes first</Checkbox>
    <span className="excel-code-preview-explanation">{preview.identityMode?'Red: existing employee matched by identity. Orange: review the reason. White: no match.':'Red: code exists in Employee Master. White: no code match.'} Excluding a row keeps it out of this import.</span>
    {preview.status === 'Checking' ? <Tag>{preview.identityMode?'Checking employee identities.':'Checking Master codes…'}</Tag> : preview.status === 'Error' ? <><Tag color="orange">{preview.identityMode?'Employee identity check unavailable':'Master-code check unavailable'}</Tag><Button size="small" onClick={preview.retry}>Retry Master code check</Button></> : <><Tag color="red">{preview.identityMode?[...preview.identities.values()].filter(row=>row.status==='Existing').length:preview.matches.size} existing {preview.identityMode?'employees':'codes'}</Tag>{preview.identityMode&&[...preview.identities.values()].some(row=>row.status==='Review')&&<Tag color="orange">{[...preview.identities.values()].filter(row=>row.status==='Review').length} review</Tag>}</>}
  </Space>
}

export function PayslipCodeMatchLabel({ code,sourceRow }: { code: string;sourceRow?:number }) {
  const preview = usePayslipCodePreview(), match = preview.matches.get(normalizePayslipCode(code))
  if (!preview.enabled || !code.trim()&&!preview.identityMode) return null
  if (preview.status !== 'Ready') return <Tag>{preview.status === 'Checking' ? 'Checking Master' : 'Not checked'}</Tag>
  if(preview.identityMode){
    const identity=preview.identities.get(sourceRow||0)
    if(!identity)return <Tag>Not checked</Tag>
    if(identity.status==='Review')return <Tooltip getPopupContainer={previewTooltipContainer} trigger={['hover','focus']} title={identity.reasons.join(' ')||'Employee identity needs review.'}><Tag color="orange" tabIndex={0}>Review</Tag></Tooltip>
    if(identity.status==='Existing')return <Tooltip getPopupContainer={previewTooltipContainer} title={`Matched by ${identity.basis.join(', ')}. Master code: ${identity.masterCode}. Existing identity and employment are checked again before Apply.`}><Tag color="red">Existing employee{identity.active?'':' (inactive)'}</Tag></Tooltip>
    return <Tag className="excel-code-no-match">No identity match</Tag>
  }
  if (!match) return <Tag className="excel-code-no-match">No code match</Tag>
  return <Tooltip title="The code exists in this client's Master. Identity and employment are checked again in Configure masters; keeping this row does not update Master until Apply."><Tag color="red">{match.records > 1 ? 'Multiple Master records—review' : match.activeRecords ? 'Existing employee' : 'Existing employee (inactive)'}</Tag></Tooltip>
}
