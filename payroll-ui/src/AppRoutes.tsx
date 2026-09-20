import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import AuthGate from './components/AuthGate'
import AppGlobalLoader from './components/AppGlobalLoader'
import ToastProvider from './components/ToastProvider'
import PublicCandidateActionPage from './pages/PublicCandidateActionPage'
import PublicCareersPage from './pages/PublicCareersPage'
import SettingsApp from './SettingsApp'
import { lazy, Suspense } from 'react'
const InternalInterviewPage = lazy(() => import('./pages/InternalInterviewPage'))

export default function AppRoutes() {
  return <BrowserRouter>
    <ToastProvider>
      <AppGlobalLoader />
      <Routes>
        <Route path="/careers/:slug" element={<PublicCareersPage />} />
        <Route path="/candidate-action/:token" element={<PublicCandidateActionPage />} />
        <Route path="/interview/:id" element={<Suspense fallback={<p>Loading secured interview…</p>}><InternalInterviewPage candidate /></Suspense>} />
        <Route path="/recruitment/interview-session/:id" element={<AuthGate><Suspense fallback={<p>Loading secured interview…</p>}><InternalInterviewPage /></Suspense></AuthGate>} />
        <Route path="/*" element={<AuthGate><Routes><Route path="/" element={<Navigate to="/dashboard" replace />} /><Route path="/*" element={<SettingsApp />} /></Routes></AuthGate>} />
      </Routes>
    </ToastProvider>
  </BrowserRouter>
}
