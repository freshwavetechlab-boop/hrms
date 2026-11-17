import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { ConfigProvider, Button, Space, Alert } from 'antd'
import theme from '/src/theme/hrmsTheme.ts'
import AuthGate from '/src/components/AuthGate.tsx'
import ToastProvider from '/src/components/ToastProvider.tsx'
import Register from '/src/components/PayslipRegister.tsx'
import '/src/index.css'
import '/src/AppShell.css'

if (!sessionStorage.getItem('register-fixture-prepared')) {
  sessionStorage.setItem('payslips.client:900', JSON.stringify(907))
  sessionStorage.setItem('payslips.run:900:907', JSON.stringify(9001))
  sessionStorage.setItem('payslips.run:900:908', JSON.stringify(9002))
  sessionStorage.setItem('register-fixture-prepared', 'true')
}
function App() {
  return <main style={{ padding: 16, height: '100vh', display: 'flex', flexDirection: 'column', gap: 12 }}>
    <h1>Mounted Payslip Register verification</h1>
    <Alert message="Synthetic records only — all API calls intercepted; no payroll, bank, mail or database writes." />
    <Space wrap>
      <Button onClick={() => fetch('/__fixture/control/fail-next', { method: 'POST' })}>Fail next detail</Button>
      <Button onClick={() => fetch('/__fixture/control/delay-next', { method: 'POST' })}>Delay next IIM detail</Button>
      <Button onClick={() => location.reload()}>Reload fixture</Button>
    </Space>
    <div id="payslips-client-controls" /><div id="payslips-page-controls" /><Register />
  </main>
}
ReactDOM.createRoot(document.getElementById('root')!).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><App /></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>)
