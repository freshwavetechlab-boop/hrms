import React, { useEffect, useState } from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter, useLocation } from 'react-router-dom'
import { Button, ConfigProvider, Segmented, Select, Space, Tag } from 'antd'
import theme from '/src/theme/hrmsTheme.ts'
import AuthGate, { useAuthSession } from '/src/components/AuthGate.tsx'
import ToastProvider from '/src/components/ToastProvider.tsx'
import AppPageHeader, { PageHeaderPortal } from '/src/components/layout/AppPageHeader.tsx'
import RecruitmentOfferJoiningWorkspace from '/src/components/RecruitmentOfferJoiningWorkspace.tsx'
import TalentAcquisitionEmployeeImport from '/src/components/TalentAcquisitionEmployeeImport.tsx'
import DataTable from '/src/components/DataTable.tsx'
import SecurityPanel from '/src/components/SecurityPanel.tsx'
import { RecruitmentViewContext } from '/src/hooks/useRecruitmentPreferences.ts'
import '/src/index.css'
import '/src/OrganizationSetup.css'
import '/src/AppShell.css'
import '/src/RecruitmentViewport.css'

function App() {
  const session = useAuthSession()
  const location = useLocation()
  const [mode, setMode] = useState('talent'), [clientId, setClientId] = useState(42), [revision, setRevision] = useState(0), [employees, setEmployees] = useState<any[]>([])
  const refresh = async () => { const response = await fetch('/__fixture/employees'); setEmployees(await response.json()) }
  useEffect(() => { void refresh() }, [])
  useEffect(() => { if(['/employees','/employees/master'].includes(location.pathname)) setMode('employees'); else if(location.pathname.includes('/security/')) setMode('users') }, [location.pathname])
  const clientName = clientId === 42 ? 'Synthetic multi-location client' : 'Other synthetic client'
  const canAccept = session?.user.permissions.some(p => ['settings.manage','recruitment.manage','recruitment.offer.issue'].includes(p)) || false
  const canImport = session?.user.permissions.includes('employees.manage')
  return <div className="joining-fixture-shell">
    <style>{`html,body,#root{height:100%;margin:0}.joining-fixture-shell{display:flex;height:100dvh;overflow:hidden}.joining-fixture-sidebar{width:210px;flex-shrink:0;padding:18px;background:#f6f8fb;border-right:1px solid #ddd}.joining-fixture-main{flex:1;display:flex;flex-direction:column;min-width:0;min-height:0}.joining-fixture-banner{padding:7px 12px;background:#e6f4ff;font-size:12px;flex-shrink:0}.joining-fixture-nav{padding:8px 12px;display:flex;gap:12px;align-items:center;flex-wrap:wrap;flex-shrink:0}.joining-fixture-workspace{flex:1;display:flex;padding:12px;min-width:0;min-height:0}.joining-fixture-workspace>section{flex:1;min-height:0}.joining-fixture-employee-table{display:flex;flex-direction:column;min-height:0;flex:1}.joining-fixture-employee-table>.data-table{flex:1;min-height:0}@media(max-width:850px){.joining-fixture-sidebar{display:none}.joining-fixture-workspace{padding:8px}}`}</style>
    <aside className="joining-fixture-sidebar">Frevo · Local verification<br /><br />Talent Acquisition<br />Employees</aside>
    <main className="joining-fixture-main"><div className="joining-fixture-banner">Synthetic browser test · In-memory API · No database · No emails</div>
      <div className="joining-fixture-nav"><Segmented aria-label="Fixture module" options={[{label:'Talent Acquisition',value:'talent'},{label:'Employees',value:'employees'},{label:'Users',value:'users'}]} value={mode} onChange={value => setMode(String(value))} /><Button size="small" onClick={async () => { await fetch('/__fixture/reset',{method:'POST'}); await refresh(); setRevision(value => value + 1) }}>Reset test data</Button><Tag>{employees.length} Employee Master records</Tag></div>
      <AppPageHeader key={mode} title={mode === 'talent' ? 'Offers & Pre-boarding' : mode === 'users' ? 'Users' : 'Employees'} description="" icon="♙" breadcrumbs={[{title: mode === 'talent' ? 'Talent Acquisition' : mode === 'users' ? 'Security' : 'Employees'},{title:mode === 'talent' ? 'Offers & Pre-boarding' : mode === 'users' ? 'Users' : 'Employee Master'}]} workspace={mode === 'talent' ? 'recruitment' : 'employees'} />
      <PageHeaderPortal slot={`${mode === 'talent' ? 'recruitment' : 'employees'}-client-controls`}><Select aria-label="Fixture client" value={clientId} options={[{value:42,label:'Synthetic multi-location client'},{value:99,label:'Other synthetic client'}]} onChange={setClientId} style={{minWidth:200}} /></PageHeaderPortal>
      <RecruitmentViewContext.Provider value={{view:'Table',scope:`joining-fixture:${session?.user.id}:${clientId}`}}>
        <div className="joining-fixture-workspace">{mode === 'users' ? <div style={{width:'100%',overflow:'auto'}}><SecurityPanel key={`users:${clientId}:${revision}`} /></div> : mode === 'talent' ? <RecruitmentOfferJoiningWorkspace key={`ta:${clientId}:${revision}`} clientId={clientId} canAccept={canAccept} onChanged={refresh} offers={<div><p>Existing offer editor and row actions remain in the normal workspace.</p><Button onClick={() => setMode('employees')}>Open Employee Master</Button></div>} /> : <section className="joining-fixture-employee-table">
          <PageHeaderPortal slot="employees-page-controls">{canImport && <TalentAcquisitionEmployeeImport clientId={clientId} clientName={clientName} onImported={refresh} />}</PageHeaderPortal>
          <DataTable rows={employees.filter(row => row.clientId === clientId)} fillHeight columns={[{key:'employeeCode',label:'Employee code'},{key:'name',label:'Employee'},{key:'source',label:'Source'}]} title="Employee Master" />
        </section>}</div>
      </RecruitmentViewContext.Provider>
    </main>
  </div>
}
ReactDOM.createRoot(document.getElementById('root')!).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><App /></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>)
