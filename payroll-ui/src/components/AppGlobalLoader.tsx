import { useEffect, useState } from 'react'
import { subscribeApiLoading } from '../services/apiClient'

export default function AppGlobalLoader() {
  const [activeRequests, setActiveRequests] = useState(0)
  const [visible, setVisible] = useState(false)

  useEffect(() => subscribeApiLoading(setActiveRequests), [])

  useEffect(() => {
    if (activeRequests <= 0) {
      setVisible(false)
      return
    }
    const timer = window.setTimeout(() => setVisible(true), 180)
    return () => window.clearTimeout(timer)
  }, [activeRequests])

  if (!visible) return null

  return <div className="app-global-loader" role="status" aria-live="polite">
    <i />
    <span>Loading...</span>
  </div>
}
