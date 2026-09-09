import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { ConfigProvider } from 'antd'
import 'antd/dist/reset.css'
import './index.css'
import AppRoutes from './AppRoutes.tsx'
import hrmsTheme from './theme/hrmsTheme.ts'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ConfigProvider theme={hrmsTheme} componentSize="middle">
      <AppRoutes />
    </ConfigProvider>
  </StrictMode>,
)
