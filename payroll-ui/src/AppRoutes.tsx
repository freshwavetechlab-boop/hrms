import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import AuthGate from './components/AuthGate'
import AppGlobalLoader from './components/AppGlobalLoader'
import ToastProvider from './components/ToastProvider'
import SettingsApp from './SettingsApp'

export default function AppRoutes() {
  return <BrowserRouter><ToastProvider><AppGlobalLoader /><AuthGate><Routes><Route path="/" element={<Navigate to="/dashboard" replace />} /><Route path="/*" element={<SettingsApp />} /></Routes></AuthGate></ToastProvider></BrowserRouter>
}
