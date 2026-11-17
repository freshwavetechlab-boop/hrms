import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { ConfigProvider } from 'antd'
import theme from '/src/theme/hrmsTheme.ts'
import AuthGate from '/src/components/AuthGate.tsx'
import ToastProvider from '/src/components/ToastProvider.tsx'
import AppPageHeader from '/src/components/layout/AppPageHeader.tsx'
import ExcelPayslips from '/src/components/ExcelPayslips.tsx'
import '/src/index.css'
import '/src/OrganizationSetup.css'
import '/src/AppShell.css'
import '/src/RecruitmentViewport.css'

function App() {
  return <div className="scope-fixture-shell">
    <style>{`
      html,body,#root{height:100%;margin:0} .scope-fixture-shell{height:100dvh;display:flex;overflow:hidden}
      .scope-fixture-sidebar{width:220px;flex-shrink:0;padding:18px;border-right:1px solid #ddd;background:#f6f8fb}
      .scope-fixture-main{min-width:0;min-height:0;flex:1;display:flex;flex-direction:column}
      .scope-fixture-banner{font-size:12px;padding:6px 12px;background:#e6f4ff;flex-shrink:0}
      .scope-fixture-workspace{display:flex;flex:1;min-width:0;min-height:0;padding:12px}
      .scope-fixture-workspace>section{flex:1;min-height:0}
      @media(max-width:850px){.scope-fixture-sidebar{display:none}.scope-fixture-workspace{padding:8px}}
    `}</style>
    <aside className="scope-fixture-sidebar">Frevo · Local verification<br /><br />Reports<br />Excel Payslips</aside>
    <main className="scope-fixture-main">
      <div className="scope-fixture-banner">Local test · Synthetic clients and salary records only · All writes blocked</div>
      <AppPageHeader title="Excel Payslips" description="" icon="▤" breadcrumbs={[{ title: 'Reports' }, { title: 'Excel Payslips' }]} workspace="excel-payslips" />
      <div className="scope-fixture-workspace"><ExcelPayslips /></div>
    </main>
  </div>
}

ReactDOM.createRoot(document.getElementById('root')!).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><App /></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>)
