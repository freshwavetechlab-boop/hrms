import React, { useRef, useState } from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { ConfigProvider, Alert } from 'antd'
import theme from '/src/theme/hrmsTheme.ts'
import AuthGate from '/src/components/AuthGate.tsx'
import ToastProvider from '/src/components/ToastProvider.tsx'
import AppPageHeader from '/src/components/layout/AppPageHeader.tsx'
import SalaryTemplateDesigner from '/src/components/SalaryTemplateDesigner.tsx'
import { mergeSalaryTemplateDraftComponents } from '/src/utils/salaryTemplateDraftComponents.ts'
import type { Component, Structure } from '/src/types/payroll.ts'
import '/src/index.css'
import '/src/OrganizationSetup.css'
import '/src/AppShell.css'
import '/src/RecruitmentViewport.css'
import fixture from '/__fixture/seed'

function App() {
  const [components, setComponents] = useState<Component[]>(fixture.components)
  const [templates, setTemplates] = useState<Structure[]>(fixture.templates)
  const [structure, setStructure] = useState<Structure>({ id: 0, name: '', clientId: '901', annualCtc: '', active: true, lines: [] })
  const [saved, setSaved] = useState(''), [error, setError] = useState('')
  const originals = useRef(JSON.stringify({ components: fixture.components, templates: fixture.templates }))
  const save = async (proposed: Component[] = []) => {
    try {
      if (!structure.id && templates.some(row => row.clientId === structure.clientId && row.name === structure.name)) throw Error('A saved template already uses this name.')
      const next = mergeSalaryTemplateDraftComponents(components, proposed)
      const row = { ...structure, id: Date.now() }
      const old = JSON.stringify({ components: next.filter(c => fixture.components.some(x => x.id === c.id)), templates: templates.filter(t => fixture.templates.some(x => x.id === t.id)) })
      if (old !== originals.current) throw Error('An existing template or shared component changed.')
      setComponents(next); setTemplates(current => [...current, row]); setSaved(row.name); setError('')
      await fetch('/__fixture/save-receipt', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ template: row, newComponents: proposed, oldRecordsUnchanged: true }) })
      return true
    } catch (failure) { setError(String(failure)); return false }
  }
  return <div className="template-fixture-shell">
    <style>{`html,body,#root{height:100%;margin:0}.template-fixture-shell{display:flex;height:100dvh;overflow:hidden}.template-fixture-sidebar{width:220px;flex-shrink:0;padding:18px;background:#f6f8fb;border-right:1px solid #ddd}.template-fixture-main{flex:1;display:flex;flex-direction:column;min-width:0;min-height:0}.template-fixture-banner{padding:6px 12px;background:#e6f4ff;font-size:12px;flex-shrink:0}.template-fixture-workspace{flex:1;display:flex;padding:12px;min-width:0;min-height:0}.template-fixture-workspace>section{flex:1;min-height:0}@media(max-width:850px){.template-fixture-sidebar{display:none}.template-fixture-workspace{padding:8px}}`}</style>
    <aside className="template-fixture-sidebar">Frevo · Local verification<br /><br />Settings<br />Salary Templates</aside>
    <main className="template-fixture-main"><div className="template-fixture-banner">Local test · Synthetic records only · Native calculator · No database or email connection</div>
      <AppPageHeader title="Salary Templates" description="" icon="▤" breadcrumbs={[{ title: 'Settings' }, { title: 'Salary Templates' }]} workspace="salary-templates" />
      {saved && <Alert type="success" message={`Fixture saved: ${saved}. Existing records unchanged.`} />}
      {error && <Alert type="error" message={error} />}
      <div className="template-fixture-workspace"><SalaryTemplateDesigner clients={fixture.clients} employees={[]} locations={fixture.locations} components={components} structure={structure} setStructure={setStructure} templates={templates} saveTemplate={save} canCreateComponentMasters /></div>
    </main>
  </div>
}
ReactDOM.createRoot(document.getElementById('root')!).render(<ConfigProvider theme={theme}><BrowserRouter><ToastProvider><AuthGate><App /></AuthGate></ToastProvider></BrowserRouter></ConfigProvider>)
