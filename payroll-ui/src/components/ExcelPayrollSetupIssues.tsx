import { Button, Space, Tag } from 'antd'
import type { SetupDiagnosis } from '../utils/excelPayrollSetupDiagnostics'

const labels: Record<SetupDiagnosis['category'], string> = {translation:'Formula mapping',source:'Excel evidence',identity:'Employee match',location:'Location',policy:'Days / policy',rounding:'Precision',salary:'Salary comparison',api:'Service update',master:'Master mapping',unknown:'Other check'}
type Props = { diagnoses: SetupDiagnosis[]; disabled: boolean; onResolve: (diagnosis: SetupDiagnosis) => void; onContinueReady?: () => void; readyCount: number; dayBasis?: string; roundingCount?: number; onReviewRounding?: () => void; sourceLabel?: string }
export default function ExcelPayrollSetupIssues({diagnoses,disabled,onResolve,onContinueReady,readyCount,dayBasis,roundingCount,onReviewRounding,sourceLabel}:Props) {
  const blockers=diagnoses.filter(item=>item.blocking),notes=diagnoses.filter(item=>!item.blocking)
  return <>
    {blockers.length>0&&<section className="excel-master-diagnostics" aria-label="Setup needs attention">
      <div className="excel-master-diagnostics-heading"><h3>Needs attention</h3><Tag color="red">{blockers.length} {blockers.length===1?'check':'checks'}</Tag></div>
      {sourceLabel&&<p className="excel-master-diagnostic-where">Source: {sourceLabel}</p>}
      {blockers.map(item=><article key={item.key} className="excel-master-diagnostic">
        <div><Space wrap size={6}><Tag>{labels[item.category]}</Tag><strong>{item.title}</strong></Space><p>{item.category==='rounding'&&roundingCount ? `${roundingCount} selected source rows have an independently verified rounding explanation. Accepting it records this exact review; original Excel amounts are unchanged. Raw differences remain in calculation details.` : item.explanation}</p>
          {(item.columns.length>0||item.sourceRows.length>0)&&<div className="excel-master-diagnostic-where"><b>Where: </b>{item.columns.join('; ')}{item.columns.length>0&&item.sourceRows.length>0?' · ':''}{item.sourceRows.length>0?`Source rows ${item.sourceRows.slice(0,8).join(', ')}${item.sourceRows.length>8?` + ${item.sourceRows.length-8} more`:''}`:''}</div>}
          {item.key==='attendance-day-inputs'&&dayBasis&&<div className="excel-master-diagnostic-where"><b>From your Excel: </b>{dayBasis}</div>}
          <details><summary>Exact check details{item.sourceRows.length?` · ${item.sourceRows.length} source rows`:''}</summary>{item.details.map((detail,index)=><p key={index}>{detail}</p>)}</details>
        </div>
        <Space direction="vertical" size={6}><Button disabled={disabled} onClick={()=>onResolve(item)}>{item.actionLabel}</Button>{item.category==='identity'&&onContinueReady&&<Button type="primary" disabled={disabled} onClick={onContinueReady}>Continue with {readyCount} ready</Button>}{item.category==='rounding'&&!!roundingCount&&onReviewRounding&&<Button type="primary" disabled={disabled} onClick={onReviewRounding}>Accept explained rounding ({roundingCount}) &amp; recheck</Button>}</Space>
      </article>)}
    </section>}
    {notes.length>0&&<details className="excel-master-details"><summary>Other information · {notes.length} notes</summary>{notes.map(item=><section key={item.key}><strong>{item.title}</strong>{item.details.map((detail,index)=><p key={index}>{detail}</p>)}</section>)}</details>}
  </>
}
