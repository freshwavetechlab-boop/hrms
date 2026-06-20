import { BrowserRouter, Route, Routes } from 'react-router-dom'
import AuthGate from './components/AuthGate'
import ToastProvider from './components/ToastProvider'
import SettingsApp from './SettingsApp'

export default function AppRoutes() {
  return <BrowserRouter><ToastProvider><AuthGate><Routes><Route path="/" element={<SettingsApp />} /><Route path="/pay-runs/history" element={<SettingsApp />} /><Route path="*" element={<SettingsApp />} /></Routes></AuthGate></ToastProvider></BrowserRouter>
}
