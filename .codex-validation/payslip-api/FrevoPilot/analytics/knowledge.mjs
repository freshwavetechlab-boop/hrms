import { existsSync } from 'node:fs'
import { readFile } from 'node:fs/promises'
import { join, relative } from 'node:path'
import { analyticsLimits, repositoryRoot } from './config.mjs'
import { rankKnowledgeChunks, retrievalVersion } from './retrieval.mjs'

const sources = [
  ['docs/analytics-business-rules.md', 'Verified HRMS analytics definitions and safeguards'],
  ['docs/api-catalog.md', 'HRMS API catalogue'],
  ['docs/enterprise-payroll-engine-audit.md', 'Payroll engine audit'],
  ['docs/payroll-income-tax-architecture.md', 'Payroll income-tax architecture'],
  ['docs/client-billing-advanced-costing-configuration.md', 'Client billing rules'],
  ['docs/recruitment-test-data.md', 'Recruitment configuration and test data'],
  ['Payroll.API/Repositories/ReportingRepository.cs', 'Canonical HRMS reports'],
  ['Payroll.API/Repositories/DashboardRepository.cs', 'Canonical dashboard metrics'],
  ['Payroll.API/Repositories/WorkflowRepository.cs', 'Workflow rules'],
  ['Payroll.API/Repositories/PayRunRepository.cs', 'Payroll run calculations and totals'],
  ['Payroll.API/Repositories/LeaveAttendanceRepository.cs', 'Attendance and leave rules'],
  ['Payroll.API/Repositories/RecruitmentTalentRepository.cs', 'Recruitment talent and ATS rules'],
  ['Payroll.API/Repositories/RecruitmentPipelineRepository.cs', 'Recruitment pipeline and SLA rules'],
  ['Payroll.API/Repositories/TravelExpenseRepository.cs', 'Travel and expense rules'],
  ['Payroll.API/Repositories/AttachmentRepository.cs', 'Attachment storage and mount rules'],
  ['Payroll.API/Repositories/ClientBillingRepository.cs', 'Client billing calculations'],
  ['Payroll.API/Repositories/OrganizationRepository.cs', 'Organization and employee master rules'],
  ['Payroll.API/Repositories/CommunicationRepository.cs', 'Employee communication rules'],
  ['Payroll.API/Repositories/TaxEngineRepository.cs', 'Income tax engine rules'],
  ['Payroll.API/Repositories/AuthRepository.cs', 'Authentication and RBAC rules'],
]

let documents = null

function splitSections(text, source, title) {
  const chunks = String(text || '').split(/\n(?=#{1,4}\s|(?:public|private|internal)\s+(?:async\s+)?(?:Task|IEnumerable|record|class)|case\s+")/g)
  return chunks
    .map((content, index) => ({ source, title, index, content: content.trim() }))
    .filter(item => item.content.length > 40)
}

export async function loadKnowledge() {
  if (documents) return documents
  const loaded = []
  for (const [path, title] of sources) {
    const absolute = join(repositoryRoot, path)
    if (!existsSync(absolute)) continue
    try {
      const content = await readFile(absolute, 'utf8')
      loaded.push(...splitSections(content, relative(repositoryRoot, absolute).replace(/\\/g, '/'), title))
    } catch {
      // A missing optional knowledge document must not stop analytics.
    }
  }
  documents = loaded
  return documents
}

export async function relevantKnowledge(question) {
  const all = await loadKnowledge()
  const ranked = rankKnowledgeChunks(question, all, 16)
  const selected = []
  let length = 0
  for (const item of ranked) {
    const excerpt = item.content.slice(0, 2400)
    if (length + excerpt.length > analyticsLimits.maxKnowledgeChars) break
    selected.push({
      source: item.source,
      title: item.title,
      content: excerpt,
      relevance: Number(item.score.toFixed(3)),
      matchedTerms: item.matchedTerms,
      domains: item.domains,
    })
    length += excerpt.length
  }
  return selected
}

export async function knowledgeStatus() {
  const loaded = await loadKnowledge()
  return {
    retrievalVersion: retrievalVersion(),
    sourceCount: new Set(loaded.map(item => item.source)).size,
    chunkCount: loaded.length,
  }
}
