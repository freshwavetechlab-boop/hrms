import { createContext, useCallback, useContext, useEffect, useRef } from 'react'
import { notification } from 'antd'
export type ToastType = 'success' | 'error' | 'warning' | 'info'
let notify: (message: string, type?: ToastType) => void = () => undefined
export const toast = {
  success: (message: string) => notify(message, 'success'),
  error: (message: string) => notify(message, 'error'),
  warning: (message: string) => notify(message, 'warning'),
    info: (message: string) => notify(message, 'info')
}
const ToastContext = createContext<(message: string, type?: ToastType) => void>((message, type) => notify(message, type))
export const useToast = () => useContext(ToastContext)
export default function ToastProvider({ children }: { children: React.ReactNode }) {
  const [api, holder] = notification.useNotification()
  const recent = useRef(new Map<string, number>())
 const show = useCallback((message: string, type: ToastType = 'success') => {

    const text = message.trim()
    if (!text) return
    const key = `${type}:${text}`
    const now = Date.now()
    if (now - (recent.current.get(key) ?? 0) < 1200) return
    recent.current.set(key, now)
    api[type]({
      key,
      message: titleFor(type),
      description: text,
      placement: 'topRight',
      duration: type === 'error' ? 5 : 3,
      className: `app-toast-notification ${type}`
    })
    }, [api])
  useEffect(() => { notify = show; return () => { notify = () => undefined } }, [show])
  return <ToastContext.Provider value={show}>{holder}{children}</ToastContext.Provider>
}

function titleFor(type: ToastType) {
  if (type === 'error') return 'Action needed'
  if (type === 'warning') return 'Please review'
  if (type === 'info') return 'Notice'
  return 'Success'
}