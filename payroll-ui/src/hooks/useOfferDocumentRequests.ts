import { useEffect, useState } from 'react'
import { getOfferDocumentRequestFields, type OfferDocumentRequestField, type OfferDocumentRequestSelection } from '../services/recruitmentOfferMailService'

export function useOfferDocumentRequests(clientId: number, enabled = true) {
  const [fields, setFields] = useState<OfferDocumentRequestField[]>([])
  const [documentRequests, setDocumentRequests] = useState<OfferDocumentRequestSelection[]>([])
  const [loadedClientId, setLoadedClientId] = useState(0)
  const [loading, setLoading] = useState(enabled), [error, setError] = useState(''), [revision, setRevision] = useState(0)
  useEffect(() => {
    let active = true
    setFields([]); setDocumentRequests([]); setError(''); setLoadedClientId(0)
    if (!enabled || !clientId) { setLoading(false); return }
    setLoading(true)
    void getOfferDocumentRequestFields(clientId).then(result => {
      if (!active) return
      const items = result.data?.items
      if (!result.ok || !Array.isArray(items) || items.some(item => !Number.isSafeInteger(item.fieldConfigurationId) || item.fieldConfigurationId <= 0 || typeof item.label !== 'string')
        || new Set(items.map(item => item.fieldConfigurationId)).size !== items.length) {
        setError(result.error || 'Unable to load the requested documents. Please try again.'); return
      }
      setFields(items)
      setDocumentRequests(items.map(item => ({ fieldConfigurationId: item.fieldConfigurationId, required: true })))
      setLoadedClientId(clientId)
      if (!items.length) setError('No requestable documents are configured. Add the document fields before emailing offers.')
    }).catch(() => { if (active) setError('Unable to load the requested documents. Please try again.') })
      .finally(() => { if (active) setLoading(false) })
    return () => { active = false }
  }, [clientId, enabled, revision])
  const current = enabled && loadedClientId === clientId
  return { fields: current ? fields : [], documentRequests: current ? documentRequests : [], setDocumentRequests, loading: loading || enabled && !current && !error, error, ready: current && !loading && !error && fields.length > 0 && documentRequests.length > 0,
    refresh: () => setRevision(value => value + 1) }
}
