import React, { useState } from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { Button, ConfigProvider, Segmented } from 'antd'
import theme from '/src/theme/hrmsTheme.ts'
import AuthGate from '/src/components/AuthGate.tsx'
import ToastProvider from '/src/components/ToastProvider.tsx'
import AppPageHeader from '/src/components/layout/AppPageHeader.tsx'
import RecruitmentTalentWorkspace from '/src/components/RecruitmentTalentWorkspace.tsx'
import RecruitmentRequisitionManager from '/src/components/RecruitmentRequisitionManager.tsx'
import BulkUploadPreviewModal, { emptyBulkUploadPreview } from '/src/components/BulkUploadPreviewModal.tsx'
import { parseImportPreviewSheets, validateImportPreview } from '/src/utils/importPreview.ts'
import { normalizeEmployeeImportDate } from '/src/utils/employeeImportDate.ts'
import { buildXlsxBlob } from '/src/utils/xlsx.ts'
import { RecruitmentViewContext } from '/src/hooks/useRecruitmentPreferences.ts'
import '/src/index.css'
import '/src/OrganizationSetup.css'
import '/src/AppShell.css'
import '/src/RecruitmentViewport.css'

function App() {
  const [mode, setMode] = useState('offers'), [view, setView] = useState('Table')
  return <div className="ta-verification">
    <style>{`html,body,#root{height:100%;margin:0}.ta-verification{height:100dvh;display:flex;overflow:hidden}.ta-verification aside{width:200px;flex-shrink:0;padding:20px;background:#f6f8fb}.ta-verification main{display:flex;flex-direction:column;flex:1;min-width:0;min-height:0}.ta-verification-banner{background:#e6f4ff;padding:8px 12px;font-size:12px}.ta-verification-nav{display:flex;flex-wrap:wrap;gap:12px;padding:8px 12px}.ta-verification-content{display:flex;flex:1;min-height:0;min-width:0;padding:12px}.ta-verification-content>section{flex:1;min-height:0;min-width:0}@media(max-width:850px){.ta-verification aside{display:none}.ta-verification-content{padding:8px}}`}</style>
    <aside>Frevo · Verification<br /><br />Talent Acquisition</aside>
    <main><div className="ta-verification-banner">Synthetic local test · In-memory API · No database · No emails</div>
      <div className="ta-verification-nav"><Segmented aria-label="Test module" value={mode} onChange={value => setMode(String(value))} options={[{label:'Offers',value:'offers'},{label:'Hiring requests',value:'requests'},{label:'Employee bulk',value:'dates'}]} /><Segmented aria-label="Test view" value={view} onChange={value => setView(String(value))} options={['Card','Table']} /></div>
      <AppPageHeader key={mode} title={mode === 'offers' ? 'Offers & Pre-boarding' : 'Hiring requests'} description="" icon="♙" breadcrumbs={[{title:'Talent Acquisition'},{title:mode}]} workspace="recruitment" />
      <RecruitmentViewContext.Provider value={{view,scope:`ta-verification:${mode}`}}><div className="ta-verification-content">{mode === 'offers' ? <RecruitmentTalentWorkspace mode="offers" initialClientId={55} /> : mode === 'requests' ? <RecruitmentRequisitionManager initialClientId={55} /> : <DatePreview />}</div></RecruitmentViewContext.Provider>
    </main>
  </div>
}
function DatePreview() {
  const [preview,setPreview] = useState(emptyBulkUploadPreview)
  const open = async () => {
    const headers=['Employee Code','First Name','Date Of Birth','Date Of Joining']
    const dates=['21/08/2026','21-08-2026','21.8.2026','21/8/26','2026-08-21','21-Aug-26','21 August 2026','08/21/2026']
    const births=['01/03/87','04/01/80','17/2/1982','15.06.1970']
    const raw=Array.from({length:16},(_,i)=>[`SYN-${i+1}`,'Synthetic employee',births[i%births.length],dates[i%dates.length]])
    const file=new File([buildXlsxBlob([{name:'Employees',rows:[headers,...raw]}])],'synthetic-mixed-employee-dates.xlsx')
    const sheet=(await parseImportPreviewSheets(file))[0]
    const rows=sheet.rows.map(row=>row.map((cell,i)=>i>=2?normalizeEmployeeImportDate(cell)??cell:cell))
    const issues=validateImportPreview({headers,rows},{dates:['Date Of Birth','Date Of Joining'],normalizeDate:normalizeEmployeeImportDate,unique:[['Employee Code']]})
    setPreview({open:true,title:'Employee bulk upload preview',fileName:file.name,headers,rows,issues})
  }
  return <section><p>Synthetic workbook · 16 employee rows · Mixed slash, dash, dot, named-month and short-year dates</p><Button onClick={()=>void open()}>Preview mixed employee dates</Button><BulkUploadPreviewModal preview={preview} onCancel={()=>setPreview(emptyBulkUploadPreview)} onConfirm={()=>setPreview(emptyBulkUploadPreview)} /></section>
}
ReactDOM.createRoot(document.getElementById('root')!).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><App /></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>)
