import { useEffect, useState } from 'react'
import { Alert, Button, Descriptions, Spin, Tag } from 'antd'
import { getPayslipMasterComparison, type ExcelPayslipRow, type PayslipMasterComparison, type PayslipIdentityMatch } from '../services/excelPayslipService'
import { payslipMasterComparisonLines, payslipMasterFieldKeys, validPayslipMasterComparison } from '../utils/excelPayslipMasterComparison'
import DataTable from './DataTable'

export default function ExcelPayslipRowDetails(p: { clientId: number; row: ExcelPayslipRow & {previewIdentity?:PayslipIdentityMatch}; compareMaster: boolean; importOpen: boolean; deliveryDetails: string }) {
  const [state, setState] = useState<{key:string;data:PayslipMasterComparison|null;error:string}>({key:'',data:null,error:''})
  const [retry, setRetry] = useState(0)
  const masterCode=p.row.previewIdentity?.status==='Existing'?p.row.previewIdentity.masterCode:p.row.employeeCode
  const identityReview=p.row.previewIdentity?.status==='Review'
  const requestKey=JSON.stringify([p.clientId,p.row.id,masterCode,payslipMasterFieldKeys(p.row),p.row.previewIdentity?.status])
  const compare=p.compareMaster && p.importOpen && (!!masterCode.trim()||!!p.row.previewIdentity)
  useEffect(()=>{
    if(!compare||identityReview||p.row.previewIdentity?.status==='No match')return
    const [clientId,,code,fields]:[number,string,string,string[]]=JSON.parse(requestKey)
    let active=true;const controller=new AbortController()
    void getPayslipMasterComparison(clientId,code,controller.signal,fields).then(result=>{
      const valid=result.ok && validPayslipMasterComparison(result.data,clientId,code)
      if(active)setState({key:requestKey,data:valid?result.data:null,error:valid?'':'Current Master details could not be checked. The Excel values are still shown; no Master changes were made.'})
    })
    return()=>{active=false;controller.abort()}
  },[compare,identityReview,requestKey,retry,p.row.previewIdentity?.status])
  const snapshot=state.key===requestKey?state.data:null,error=state.key===requestKey?state.error:''
  const lines=payslipMasterComparisonLines(p.row,snapshot)
  return <section aria-label="Source employee details">
    <Descriptions bordered column={1} size="small">{[
      {key:'source',label:'Source row',children:p.row.sourceRow},
      ...(!compare?[{key:'email',label:'Email',children:p.row.email||'Not supplied'}]:[]),
      {key:'delivery',label:'Delivery details',children:p.deliveryDetails},
      ...(!compare?[{key:'code',label:'Employee code',children:p.row.employeeCode||(p.importOpen?'Auto on save':'Not supplied')},...p.row.information.map((item,index)=>({key:`info-${index}`,label:item.label,children:item.value||'-'}))]:[]),
    ].map(item=><Descriptions.Item key={item.key} label={item.label}>{item.children}</Descriptions.Item>)}</Descriptions>
    {compare && <section aria-label="Master versus Excel comparison">
      <Alert type="info" showIcon message="Old: Current Master · New: This Excel" description="This is a read-only field comparison. Existing personal, contact and bank fields are kept by salary import. Salary components and earned/net amounts are compared separately in the salary setup review." />
      {!snapshot&&!error&&!identityReview&&p.row.previewIdentity?.status!=="No match"&&<Spin tip="Reading current Master values…"><div style={{minHeight:40}} /></Spin>}
      {identityReview&&<Alert type="warning" message="Employee identity needs review" description={p.row.previewIdentity?.reasons.join(" " )} />}
      {p.row.previewIdentity?.status==="No match"&&<Alert type="info" message="No existing employee identity matched this source row." />}
      {error&&<Alert type="warning" message={error} action={<Button size="small" onClick={()=>setRetry(v=>v+1)}>Retry Master comparison</Button>} />}
      {snapshot?.status==='Not found'&&<Alert type="info" message="No matching Master code. Employee identity is reviewed in Configure masters." />}
      {snapshot?.status==='Ambiguous'&&<Alert type="warning" message="This code matches multiple Master records. No old record was selected; resolve the identity in Configure masters." />}
      <DataTable title="Employee fields: Old and New" rows={lines} getRowId={row=>row.id} exportDisabled pageSizeOptions={[50,100]} columns={[
        {key:'field',label:'Field',width:180,wrap:true},
        {key:'oldValue',label:'Old — Current Master',width:230,wrap:true,render:line=>line.oldValue==null?'—':line.oldValue.trim()?line.oldValue:'Not set'},
        {key:'newValue',label:'New — This Excel',width:230,wrap:true,render:line=>line.newValue.trim()?line.newValue:'Not supplied'},
        {key:'status',label:'Comparison',width:140,wrap:true,render:line=><Tag color={line.status==='Same'?'green':line.status==='Different'?'orange':line.status==='New value'?'blue':undefined}>{line.status}</Tag>},
      ]} />
    </section>}
  </section>
}
