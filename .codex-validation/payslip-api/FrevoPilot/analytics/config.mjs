import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const here = dirname(fileURLToPath(import.meta.url))
export const repositoryRoot = process.env.FREVOPILOT_KNOWLEDGE_ROOT || resolve(here, '../../..')
export const analyticsLimits = Object.freeze({ maxRows: 500, maxExecutionMs: 15000, maxSchemaTables: 12, maxKnowledgeChars: 8000 })
