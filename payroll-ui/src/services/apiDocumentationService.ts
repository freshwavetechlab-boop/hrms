import { apiRequest, getJsonResult } from './apiClient'

export type ApiOperation = { summary?: string; description?: string; operationId?: string; tags?: string[]; security?: unknown[]; [key: string]: unknown }
export type ApiDocument = { openapi: string; info: { title: string; version: string }; paths: Record<string, Record<string, ApiOperation>>; components?: Record<string, unknown>; [key: string]: unknown }
export const getApiDocument = () => getJsonResult<ApiDocument | null>('/api/openapi/v1.json', null, { toast: false })

export type SwaggerRequest = { url: string; headers: Record<string, string>; credentials: 'include' | 'omit' }
type SwaggerInstance = { getSystem?: () => { fn?: { fetch?: { withCredentials?: boolean } } } }
declare global { interface Window { SwaggerUIBundle?: (options: Record<string, unknown>) => SwaggerInstance } }
let assets: Promise<void> | undefined

// Reuse Swagger shipped with the API; no second documentation or UI dependency.
export function loadSwaggerAssets() {
  return assets ??= Promise.all(['swagger-ui.css', 'swagger-ui-bundle.js'].map(async file => {
    const response = await apiRequest('/api/docs/' + file, { loader: false })
    if (!response.ok) throw new Error('Unable to load the API explorer (' + response.status + ').')
    const url = URL.createObjectURL(new Blob([await response.text()], { type: file.endsWith('.css') ? 'text/css' : 'text/javascript' }))
    await new Promise<void>((resolve, reject) => {
      const node = file.endsWith('.css') ? document.createElement('link') : document.createElement('script')
      if (node instanceof HTMLLinkElement) { node.rel = 'stylesheet'; node.href = url } else node.src = url
      node.onload = () => { URL.revokeObjectURL(url); resolve() }
      node.onerror = () => { URL.revokeObjectURL(url); node.remove(); reject(new Error('Unable to load the API explorer.')) }
      document.head.append(node)
    })
  })).then(() => {}).catch(error => { assets = undefined; throw error })
}
