import ClLwpSalaryImpact from './ClLwpSalaryImpact'
import { useState } from 'react'
import { Alert, Button, Checkbox, Descriptions, Drawer, Form, Input, InputNumber, Select, Space, Tag } from 'antd'
import { useAuthSession } from './AuthGate'
import { PageHeaderPortal } from './layout/AppPageHeader'
import { emptyClLwpScenario,loadClLwpDraft,saveClLwpDraft,simulateClLwp,type ClLwpScenario } from '../utils/clLwpSimulation'
import './ClLwpPolicySimulator.css'

export default function ClLwpPolicySimulator({clientId}:{clientId:number}) {
  const session=useAuthSession(),[open,setOpen]=useState(false)
  if(!session || clientId<=0 || session.user.clientId && session.user.clientId!==clientId)return null
  const canSave=session.user.permissions.some(p=>['settings.manage','client.settings.manage'].includes(p))
  return <><PageHeaderPortal slot="attendance-settings-page-controls"><Button onClick={()=>setOpen(true)}>CL / LWP simulator</Button></PageHeaderPortal>{open && <Simulator key={`${session.user.id}:${clientId}`} clientId={clientId} userId={session.user.id} canSave={canSave} onClose={()=>setOpen(false)}/>}</>
}
function Simulator({clientId,userId,canSave,onClose}:{clientId:number;userId:number;canSave:boolean;onClose:()=>void}) {
  const [initial]=useState(()=>{try{const draft=loadClLwpDraft(window.localStorage,userId,clientId);return {scenario:draft?.scenario||emptyClLwpScenario(clientId),revision:draft?.revision||0,error:''}}catch(e){return {scenario:emptyClLwpScenario(clientId),revision:0,error:e instanceof Error?e.message:'Browser draft unavailable.'}}})
  const [scenario,setScenario]=useState(initial.scenario),[revision,setRevision]=useState(initial.revision),[error,setError]=useState(initial.error),[notice,setNotice]=useState('')
  const result=simulateClLwp(scenario)
  const patch=(change:Partial<ClLwpScenario>)=>{setScenario(s=>({...s,...change}));setNotice('')}
  const number=(label:string,key:Exclude<{[K in keyof ClLwpScenario]:ClLwpScenario[K] extends number|null?K:never}[keyof ClLwpScenario],'clientId'>,max=31)=><Form.Item label={label}><InputNumber aria-label={label} min={0} max={max} step={0.5} value={scenario[key]} onChange={value=>patch({[key]:value})} style={{width:'100%'}} placeholder="Unknown — enter explicitly"/></Form.Item>
  const select=<K extends keyof ClLwpScenario>(label:string,key:K,options:ClLwpScenario[K][]) => <Form.Item label={label}><Select aria-label={label} value={scenario[key] as string} options={options.map(value=>({value:String(value),label:String(value)}))} onChange={value=>patch({[key]:value})}/></Form.Item>
  const save=()=>{try{const draft=saveClLwpDraft(window.localStorage,userId,scenario,revision);setRevision(draft.revision);setError('');setNotice(`Browser draft v${draft.revision} saved for this signed-in user/client. No server policy, entitlement, balance or payroll was changed.`)}catch(e){setError(e instanceof Error?e.message:'Unable to save browser draft.')}}
  return <Drawer open title="Interim CL / LWP policy simulator" width={1080} contentWrapperStyle={{ maxWidth: '98vw' }} rootClassName="cl-lwp-simulator-root" className="cl-lwp-policy-simulator" onClose={onClose} footer={<Space wrap><Button onClick={onClose}>Close</Button><Button disabled={!canSave} onClick={save}>Save browser draft</Button><Button onClick={()=>{try{const draft=loadClLwpDraft(window.localStorage,userId,clientId);setScenario(draft?.scenario||emptyClLwpScenario(clientId));setRevision(draft?.revision||0);setError('');setNotice('Browser draft reopened; unsaved inputs replaced. No real policy changed.')}catch(e){setError(e instanceof Error?e.message:'Unable to reopen draft.')}}}>Reopen saved draft</Button><Button disabled title="Exact client rules and authorized policy approval are required. This simulator has no activation API.">Activate policy — approval required</Button></Space>}>
    <div className="cl-lwp-simulator-content">
      <Alert showIcon type="warning" message="Comparison assumption — policy approval pending" description="CL and LWP only. This proposes a reviewable interim rule; no annual allowance is preselected. Simulation has no ledger, master, attendance or payroll side effects. Browser drafts are local to this device/user/client, not published policy versions."/>
      <Space wrap><Tag>Client {clientId}</Tag><Tag>Draft v{revision}</Tag><Tag>{result.policyBasis}</Tag><Tag>{result.reviewStatus}</Tag></Space>
      {error && <Alert showIcon type="error" message={error}/>} {notice && <Alert showIcon type="success" message={notice}/>}
      <Form component="div" layout="vertical">
        <div className="cl-lwp-simulator-grid">
          <Form.Item label="Proposed effective date"><Input aria-label="Proposed effective date" type="date" value={scenario.effectiveFrom} onChange={e=>patch({effectiveFrom:e.target.value})}/></Form.Item>
          <Form.Item label="Cycle start"><Input aria-label="Cycle start" type="date" value={scenario.cycleStart} onChange={e=>patch({cycleStart:e.target.value})}/></Form.Item>
          <Form.Item label="Cycle end"><Input aria-label="Cycle end" type="date" value={scenario.cycleEnd} onChange={e=>patch({cycleEnd:e.target.value})}/></Form.Item>
          {select('Attendance meaning','basis',['Unknown','Worked','Already payable'])}
          {select('Join/exit eligibility','eligibility',['Unknown','Full cycle','Reviewed partial cycle'])}
          {scenario.eligibility==='Reviewed partial cycle' && number('Reviewed eligible calendar days','eligibleDays')}
          {number(scenario.basis==='Already payable'?'Entered already-payable days':'Actual worked days','attendanceDays')}
          {number('Salary divisor','divisor')}
          {select('Input evidence state','evidence',['Unknown','Assumed','User reviewed source'])}
          {scenario.basis==='Worked' && <>
            {number('Eligible paid weekly-off days (non-overlapping)','paidWeeklyOff')}{number('Eligible paid holiday days (non-overlapping)','paidHolidays')}
            {number('Proposed CL limit','proposedLimit',366)}{select('CL limit period','limitPeriod',['Unknown','Annual','Monthly','Cycle'])}
            {number('Already used CL in limit period','usedInLimitPeriod',366)}{number('Available CL balance for scenario','availableCl',366)}{number('Requested paid CL allocation','requestedCl')}
            {select('CL approval assumption','approval',['Unknown','Assume approved','Reviewed approved days'])}
            {scenario.approval==='Reviewed approved days' && number('Reviewed approved CL days','approvedCl')}
            <Form.Item label="Remaining absence treatment"><Select aria-label="Remaining absence treatment" value={scenario.excessAsLwp===null?'Unknown':scenario.excessAsLwp?'LWP':'Block'} options={['Unknown','LWP','Block'].map(value=>({value,label:value}))} onChange={value=>patch({excessAsLwp:value==='Unknown'?null:value==='LWP'})}/></Form.Item>
          </>}
          {scenario.basis==='Already payable' && <>{number('Included CL annotation (never added again)','includedCl')}<Form.Item label="Avoid double counting"><Checkbox checked={scenario.requestedCl===null || scenario.requestedCl===0} onChange={event=>{if(event.target.checked)patch({requestedCl:null})}}>No additional CL is added to already-payable attendance</Checkbox></Form.Item></>}
        </div>
        <details><summary>Source Excel comparison (original formulas/values remain unchanged)</summary><div className="cl-lwp-simulator-grid">
          <Form.Item label="File / sheet / cell evidence only"><Input aria-label="Source evidence reference" maxLength={240} value={scenario.sourceReference} onChange={e=>patch({sourceReference:e.target.value})} placeholder="No employee names, identifiers or bank data"/></Form.Item>
          {number('Source payable days','sourcePayable')}{number('Source LWP days','sourceLwp')}{number('Source divisor','sourceDivisor')}
        </div></details>
      </Form>
      <section aria-label="CL LWP comparison result"><Alert type={result.errors.length?'warning':'info'} showIcon message={result.status}/>
        <Descriptions bordered size="small" column={{xs:1,sm:2,md:3}}>{[
          {key:'cycle',label:'Cycle / eligible days',children:`${result.cycleDays??'Unknown'} / ${result.eligibleDays??'Unknown'}`},
          {key:'absence',label:'Comparable absence',children:result.absenceDays??'Unknown'},
          {key:'cl',label:'Scenario paid CL (not consumed)',children:result.paidCl??'Unknown'},
          {key:'lwp',label:'Source → scenario LWP',children:`${scenario.sourceLwp??'Unknown'} → ${result.lwp??'Unknown'}`},
          {key:'payable',label:'Source → scenario payable',children:`${scenario.sourcePayable??'Unknown'} → ${result.payable??'Unknown'}`},
          {key:'divisor',label:'Source → proposed divisor',children:`${scenario.sourceDivisor??'Unknown'} → ${result.divisor??'Unknown'}`},
          {key:'delta',label:'Payable / LWP day variance',children:`${result.payableVariance??'Unknown'} / ${result.lwpVariance??'Unknown'}`},
        ].map(item=><Descriptions.Item key={item.key} label={item.label}>{item.children}</Descriptions.Item>)}</Descriptions>
        {result.errors.map(reason=><Alert key={reason} type="error" message={reason}/>)}
        <ul>{result.reasons.map(reason=><li key={reason}>{reason}</li>)}</ul>
        <ClLwpSalaryImpact scenario={scenario} days={result} />
      </section>
    </div>
  </Drawer>
}
