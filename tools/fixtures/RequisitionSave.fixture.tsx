import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { ConfigProvider } from 'antd'
import theme from '/src/theme/hrmsTheme.ts'
import AuthGate from '/src/components/AuthGate.tsx'
import ToastProvider from '/src/components/ToastProvider.tsx'
import AppPageHeader from '/src/components/layout/AppPageHeader.tsx'
import RecruitmentRequisitionManager from '/src/components/RecruitmentRequisitionManager.tsx'
import '/src/index.css'
import '/src/OrganizationSetup.css'
import '/src/AppShell.css'
import '/src/RecruitmentViewport.css'

function App() {
  return <main className="fixture"><style>{`html,body,#root{margin:0;height:100%;}.fixture{height:100dvh;display:flex;flex-direction:column;padding:12px;box-sizing:border-box;min-width:0}.fixture-banner{font-size:12px;background:#e6f4ff;padding:10px;margin-bottom:10px}.fixture-workspace{flex:1;min-height:0;min-width:0;display:flex;flex-direction:column}`}</style>
    <div className="fixture-banner">Synthetic local hiring-request test · No production data, database or emails · Case: {new URLSearchParams(location.search).get('case') || 'jd-network'}</div>
    <AppPageHeader /><section className="fixture-workspace"><RecruitmentRequisitionManager initialClientId={55} clientScopeManaged initialRequisitionId={123} /></section>
  </main>
}
ReactDOM.createRoot(document.getElementById('root')!).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><App /></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>)
