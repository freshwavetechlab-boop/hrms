import React, { useState } from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { ConfigProvider, Segmented } from 'antd'
import theme from '/src/theme/hrmsTheme.ts'
import AuthGate from '/src/components/AuthGate.tsx'
import ToastProvider from '/src/components/ToastProvider.tsx'
import AppPageHeader from '/src/components/layout/AppPageHeader.tsx'
import EmployeePage from '/src/pages/EmployeePage.tsx'
import SalaryTemplateDesigner from '/src/components/SalaryTemplateDesigner.tsx'
import { component0, structure0 } from '/src/data/payrollDefaults.ts'
import '/src/index.css'
import '/src/OrganizationSetup.css'
import '/src/AppShell.css'
import '/src/RecruitmentViewport.css'

const clients = [{ id: 17, name: 'Synthetic Research Client', code: 'SYN-RESEARCH', isActive: true }, { id: 33, name: 'Synthetic Other Client', code: 'SYN-OTHER', isActive: true }]
const components = [
  { ...component0, id: 1, code: 'BASIC', name: 'Basic', value: '10000', priority: '1' },
  { ...component0, id: 2, code: 'HRA', name: 'HRA', value: '3000', priority: '2' },
]
const templates = [
  { id: 701, clientId: '33', name: 'Other client monthly salary', annualCtc: '0', active: true, lines: [{ componentId: '1', value: '99999' }, { componentId: '2', value: '20000' }] },
  ...(new URLSearchParams(location.search).has('eligible') ? [{ id: 702, clientId: '17', name: 'Research client monthly salary', annualCtc: '0', active: true, lines: [{ componentId: '1', value: '10000' }, { componentId: '2', value: '3000' }] }] : []),
]

function App() {
  const [mode, setMode] = useState('employees')
  const [structure, setStructure] = useState(structure0)
  return <main className="salary-selection-fixture">
    <style>{`html,body,#root{height:100%;margin:0}.salary-selection-fixture{height:100dvh;display:flex;flex-direction:column;min-width:0;overflow:hidden}.salary-selection-banner{background:#e6f4ff;padding:8px 12px;font-size:12px}.salary-selection-nav{padding:8px 12px}.salary-selection-content{flex:1;min-height:0;display:flex;flex-direction:column;overflow:auto;padding:12px}.salary-selection-content>section{flex:1;min-height:0}`}</style>
    <div className="salary-selection-banner">Synthetic local verification · All APIs in memory · No database, production requests or emails</div>
    <div className="salary-selection-nav"><Segmented aria-label="Fixture workspace" value={mode} onChange={value => setMode(String(value))} options={[{ label: 'Employee Master', value: 'employees' }, { label: 'Salary Templates', value: 'templates' }]} /></div>
    <AppPageHeader key={mode} title={mode === 'employees' ? 'Employee Master' : 'Salary Templates'} description="Synthetic salary selection verification" />
    <div className="salary-selection-content">{mode === 'employees' ? <EmployeePage /> : <SalaryTemplateDesigner fixedClientId={17} clients={clients} components={components} structure={structure} setStructure={setStructure} templates={templates} saveTemplate={() => false} />}</div>
  </main>
}
ReactDOM.createRoot(document.getElementById('root')!).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><App /></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>)
