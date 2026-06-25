import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

const apiTarget = process.env.VITE_API_URL ?? `http://localhost:${process.env.PAYROLL_API_PORT ?? '5062'}`

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': apiTarget
    }
  }
})
