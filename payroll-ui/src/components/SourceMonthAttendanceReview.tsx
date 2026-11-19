import { useEffect, useRef, useState } from 'react'
import { Alert, Button, Checkbox, Modal, Space, Tag } from 'antd'
import type { ExcelPayslipBatch } from '../services/excelPayslipService'
import type { PayrollSetupRequest } from '../services/excelPayrollSetupService'
import { previewSourceMonthAttendance, saveSourceMonthAttendance, sourcePeriodStatutoryCapabilities, type SourceMonthAttendancePreview } from '../services/sourcePeriodStatutoryService'
import { excelColumnLetter } from '../utils/excelPayslipImport'
import DataTable from './DataTable'

export default function SourceMonthAttendanceReview({clientId,batch,setup,disabled}:{clientId:number;batch:ExcelPayslipBatch;setup:PayrollSetupRequest;disabled:boolean}) {
  const [open,setOpen]=useState(false),[busy,setBusy]=useState(false),[error,setError]=useState(''),[confirmed,setConfirmed]=useState(false)
  const [extra,setExtra]=useState(false),[plans,setPlans]=useState<{preview:SourceMonthAttendancePreview;requestId:string}[]>([])
  const [completed,setCompleted]=useState<number[]>([]),saved=useRef(new Set<number>())
  const selected=batch.rows.filter(row=>setup.rowIds.includes(row.id))
  const scope=JSON.stringify({clientId,batch:batch.id,month:batch.month,rows:setup.rowIds,divisor:setup.payrollDaysColumnIndex,payable:setup.payableDaysColumnIndex,decisions:setup.employeeDecisions})
  const reset=()=>{setPlans([]);setCompleted([]);saved.current.clear()}
  useEffect(()=>{reset();setConfirmed(false);setExtra(false)},[scope]) // eslint-disable-line react-hooks/exhaustive-deps
  const review=async()=>{
    if(busy||!confirmed)return
    setBusy(true);setError('');reset()
    try {
      const capabilities=await sourcePeriodStatutoryCapabilities(clientId)
      if(!capabilities.ok||capabilities.data?.sourceAttendanceOnlySupported!==true)throw Error('The updated API is required for attendance-only import. No salary setup save is used.')
      if(!capabilities.data.payableOnlyAttendanceEnabled)throw Error('The compatible payable-only API/worker rollout must be enabled before source attendance import.')
      if(clientId!==batch.clientId||setup.locationAllocations||setup.payrollDaysColumnIndex<0||setup.payableDaysColumnIndex<0||setup.payrollDaysColumnIndex===setup.payableDaysColumnIndex)
        throw Error('Choose this batch’s distinct source divisor and payable columns. Location allocations retain their separate flow.')
      const rows=selected.map(row=>{
        const employeeId=row.employment?.employeeId??setup.employeeDecisions.find(decision=>decision.rowId===row.id)?.matchedEmployeeId
        if(!employeeId)throw Error(`Source row ${row.sourceRow}: resolve its existing Employee Master match first.`)
        return {employeeId,sourceRowId:row.id}
      })
      if(!rows.length||rows.length>1000)throw Error('Select between 1 and 1000 existing source employees.')
      const results:typeof plans=[]
      for(let offset=0;offset<rows.length;offset+=100){
        const result=await previewSourceMonthAttendance({version:1,clientId,payPeriod:batch.month,sourceBatchId:batch.id,sourceHash:'',
          divisorColumn:setup.payrollDaysColumnIndex,payableColumn:setup.payableDaysColumnIndex,cycleStartDay:setup.attendanceCycleStartDay,cycleEndDay:setup.attendanceCycleEndDay,
          reviewedExtraPaidUnits:extra,reason:'Final source salary divisor and payable units reviewed; actual physical presence remains unknown.',rows:rows.slice(offset,offset+100)})
        if(!result.ok||!result.data)throw Error(result.error||'Source attendance review failed.')
        if(result.data.request.clientId!==clientId||result.data.request.sourceBatchId!==batch.id||result.data.request.payPeriod!==batch.month)throw Error('Attendance preview belongs to another scope.')
        results.push({preview:result.data,requestId:crypto.randomUUID().replaceAll('-','')})
      }
      setPlans(results)
    }catch(failure){setError(failure instanceof Error?failure.message:'Attendance preview failed.')}
    finally{setBusy(false)}
  }
  const apply=async()=>{
    if(busy||!plans.length||plans.some(plan=>!plan.preview.canSave))return
    setBusy(true);setError('')
    try {
      for(let index=0;index<plans.length;index++){
        if(saved.current.has(index))continue
        const plan=plans[index],result=await saveSourceMonthAttendance(plan.preview,plan.requestId)
        if(!result.ok||!result.data)throw Error(result.error||'Attendance save could not be confirmed. Retry this same review; confirmed groups are retained.')
        if(result.data.requestId!==plan.requestId||result.data.savedEmployees!==plan.preview.request.rows.length)throw Error('Attendance response differs from this reviewed group.')
        saved.current.add(index);setCompleted([...saved.current])
      }
    }catch(failure){setError(failure instanceof Error?failure.message:'Attendance save failed.')}
    finally{setBusy(false)}
  }
  const count=completed.reduce((total,index)=>total+plans[index].preview.request.rows.length,0)
  return <>
    <Button disabled={disabled||!selected.length} onClick={()=>{setOpen(true);setError('')}}>Import source attendance only</Button>
    {open&&<Modal open centered width={950} title={`Source attendance · ${batch.clientName} · ${batch.month}`} bodyStyle={{maxHeight:'65dvh',overflowY:'auto',minWidth:0}} maskClosable={!busy} closable={!busy} onCancel={()=>{if(!busy)setOpen(false)}} footer={<Space wrap><Button disabled={busy} onClick={()=>setOpen(false)}>Close</Button><Button loading={busy} disabled={!confirmed||count>0} onClick={()=>void review()}>Review source attendance</Button><Button type="primary" loading={busy} disabled={!plans.length||plans.some(plan=>!plan.preview.canSave)||count===selected.length} onClick={()=>void apply()}>Save reviewed attendance</Button></Space>}>
      <p>Only missing monthly attendance is inserted. Existing salary assignments, formulas, attendance policies and payroll stay unchanged. Review PF/ESIC/PT separately after this step.</p>
      <p>Divisor column: {excelColumnLetter(setup.payrollDaysColumnIndex)} · Payable column: {excelColumnLetter(setup.payableDaysColumnIndex)}</p>
      <Space direction="vertical"><Checkbox checked={confirmed} disabled={busy||count>0} onChange={event=>{setConfirmed(event.target.checked);reset()}}>These are final source salary units; actual physical presence is unknown.</Checkbox><Checkbox checked={extra} disabled={busy||count>0} onChange={event=>{setExtra(event.target.checked);reset()}}>Allow source paid units above the salary divisor at the saved salary rate</Checkbox></Space>
      {!!plans.length&&<><p><Tag color={count===selected.length?'green':'blue'}>{count} of {selected.length} attendance rows saved</Tag></p>{plans.flatMap(plan=>plan.preview.blockers).map((message,index)=><Alert key={index} type="warning" message={message}/>)}<DataTable title="Source salary units" rows={plans.flatMap(plan=>plan.preview.rows)} getRowId={row=>String(row.employeeId)} exportDisabled hideSearch columns={[{key:'employeeCode',label:'Employee code'},{key:'divisor',label:'Salary divisor'},{key:'payableUnits',label:'Payable units'},{key:'presence',label:'Actual present',value:()=> 'Unknown'},{key:'blockers',label:'Review',value:row=>row.blockers.join('; ')||'Ready'}]}/></>}
      {error&&<Alert type="error" message={error}/>}
    </Modal>}
  </>
}
