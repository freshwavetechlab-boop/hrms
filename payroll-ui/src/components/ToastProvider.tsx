import { createContext, useContext, useState } from 'react'
export type ToastType = 'success' | 'error' | 'warning' | 'info'
type Toast = { id: number; message: string; type: ToastType }
let notify: (message: string, type?: ToastType) => void = () => undefined
export const toast = {
  success: (message: string) => notify(message, 'success'),
  error: (message: string) => notify(message, 'error'),
  warning: (message: string) => notify(message, 'warning'),
  info: (message: string) => notify(message, 'info')
}
const ToastContext = createContext<(message: string, type?: ToastType) => void>((message, type) => notify(message, type))
export const useToast = () => useContext(ToastContext)
export default function ToastProvider({ children }: { children: React.ReactNode }) { const [items, setItems] = useState<Toast[]>([]); const show = (message: string, type: ToastType = 'success') => { const id = Date.now() + Math.random(); setItems(current => [...current, { id, message, type }]); window.setTimeout(() => setItems(current => current.filter(item => item.id !== id)), 5000) }; notify = show; return <ToastContext.Provider value={show}>{children}<div className="toast-stack">{items.map(item => <div className={`toast ${item.type}`} key={item.id}>{item.message}</div>)}</div></ToastContext.Provider> }
