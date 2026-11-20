import React from 'react'
import ReactDOM from 'react-dom/client'
import {BrowserRouter} from 'react-router-dom'
import {ConfigProvider} from 'antd'
import theme from '/src/theme/hrmsTheme.ts'
import AuthGate from '/src/components/AuthGate.tsx'
import ToastProvider from '/src/components/ToastProvider.tsx'
import AppPageHeader from '/src/components/layout/AppPageHeader.tsx'
import EmployeePage from '/src/pages/EmployeePage.tsx'
import '/src/index.css'
import '/src/OrganizationSetup.css'
import '/src/AppShell.css'
function App(){return <div className="employee-loading-fixture"><style>{`html,body,#root{height:100%;margin:0}.employee-loading-fixture{height:100dvh;display:flex;flex-direction:column;min-height:0}.employee-loading-fixture>section{flex:1;min-height:0}.employee-loading-fixture-note{padding:8px 14px;background:#e6f4ff;color:#134b67;font-size:12px;flex:0 0 auto}`}</style><div className="employee-loading-fixture-note">Synthetic loading test · Employee API delayed 1.5s · Manage setup delayed 4s · No production database or emails</div><AppPageHeader title="Employee Master" description="" icon="♙" breadcrumbs={[{title:'Employees'},{title:'Employee Master'}]} workspace="employees"/><EmployeePage/></div>}
ReactDOM.createRoot(document.getElementById('root')!).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><App/></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>)
