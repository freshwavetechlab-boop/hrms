import { createContext, useContext, useState } from 'react'
const ToastContext = createContext<(message: string) => void>(() => undefined)
export const useToast = () => useContext(ToastContext)
export default function ToastProvider({ children }: { children: React.ReactNode }) { const [items, setItems] = useState<{ id: number; message: string }[]>([]); const show = (message: string) => { const id = Date.now(); setItems(current => [...current, { id, message }]); window.setTimeout(() => setItems(current => current.filter(item => item.id !== id)), 3500) }; return <ToastContext.Provider value={show}>{children}<div className="toast-stack">{items.map(item => <div className="toast" key={item.id}>OK {item.message}</div>)}</div></ToastContext.Provider> }
