const stopWords = new Set([
  'a', 'an', 'and', 'are', 'as', 'at', 'be', 'by', 'for', 'from', 'has', 'have',
  'how', 'in', 'is', 'it', 'me', 'of', 'on', 'or', 'per', 'show', 'that', 'the',
  'this', 'to', 'use', 'what', 'which', 'with', 'wise',
  'aur', 'batao', 'btado', 'dikhao', 'dikhaiye', 'hai', 'hain', 'hisaab', 'ka',
  'ke', 'ki', 'ko', 'karo', 'karna', 'mein', 'mujhe', 'se', 'wala', 'wale',
])

const canonicalToken = new Map(Object.entries({
  aplicant: 'applicant',
  aplicants: 'applicant',
  aplications: 'application',
  aplication: 'application',
  aprove: 'approval',
  aproval: 'approval',
  attendence: 'attendance',
  candiate: 'candidate',
  candiates: 'candidate',
  claint: 'client',
  clint: 'client',
  expence: 'expense',
  expnse: 'expense',
  hirring: 'hiring',
  invoce: 'invoice',
  recrutment: 'recruitment',
  salry: 'salary',
  statment: 'statement',
  workflw: 'workflow',
  lwp: 'lop',
  employees: 'employee',
  candidates: 'candidate',
  applications: 'application',
  approvals: 'approval',
  claims: 'claim',
  components: 'component',
  configurations: 'configuration',
  deductions: 'deduction',
  earnings: 'earning',
  invoices: 'invoice',
  policies: 'policy',
  requests: 'request',
  resumes: 'resume',
  scores: 'score',
  servers: 'server',
  tasks: 'task',
  workflows: 'workflow',
}))

export const domainLexicon = Object.freeze({
  workforce: ['employee', 'workforce', 'headcount', 'staff', 'manpower', 'designation', 'department', 'grade', 'joining'],
  payroll: ['payroll', 'payrun', 'salary', 'wage', 'ctc', 'netpay', 'net', 'earning', 'deduction', 'component', 'payslip', 'arrear'],
  attendance: ['attendance', 'punch', 'present', 'absent', 'payable', 'lop', 'regularization', 'shift', 'overtime'],
  leave: ['leave', 'balance', 'entitlement', 'holiday', 'accrual', 'encashment'],
  recruitment: ['recruitment', 'hiring', 'candidate', 'application', 'ats', 'resume', 'interview', 'offer', 'position', 'requisition', 'shortlist'],
  workflow: ['workflow', 'approval', 'approver', 'task', 'stage', 'bottleneck', 'pending', 'sla'],
  travelExpense: ['travel', 'expense', 'claim', 'advance', 'trip', 'receipt', 'reimbursement'],
  attachment: ['attachment', 'storage', 'server', 'mount', 'drive', 'google', 'document', 'upload', 'health'],
  billing: ['billing', 'invoice', 'rate', 'ratecard', 'gst', 'commission', 'costing', 'servicefee'],
  tax: ['tax', 'tds', 'income', 'regime', 'declaration', 'proof', 'exemption', 'deduction'],
  security: ['security', 'role', 'permission', 'rbac', 'login', 'audit', 'access'],
  communication: ['communication', 'email', 'sms', 'whatsapp', 'message', 'notification', 'template'],
})

const relationshipRules = Object.freeze([
  'employees.ClientId -> clients.Id',
  'payruns.ClientId -> clients.Id',
  'employee_monthly_attendance.client_id -> clients.Id',
  'employee_monthly_attendance.employee_id -> employees.Id',
  'essleaverequests.ClientId -> clients.Id',
  'essleaverequests.EmployeeId -> employees.Id',
  'recruitment_candidate_applications.ClientId -> clients.Id',
  'recruitment_candidate_applications.CandidateId -> recruitment_candidates.Id',
  'recruitment_application_scores.ApplicationId -> recruitment_candidate_applications.Id',
  'workflowtasks.InstanceId -> workflowinstances.Id',
  'workflowinstances.WorkflowId -> workflowmasters.Id',
  'workflowmasters.Code -> workflowactivities.ActivityCode',
  'ess_expense_claims.ClientId -> clients.Id',
  'ess_expense_claims.EmployeeId -> employees.Id',
  'ess_travel_requests.ClientId -> clients.Id',
  'client_billing_configurations.ClientId -> clients.Id',
  'client_billing_cost_rule_headers.ClientId -> clients.Id',
  'entity_attachments.storage_server_id -> attachment_storage_servers.id',
])

function baseNormalize(value) {
  return String(value || '')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, ' ')
    .trim()
}

function normalizeToken(value) {
  const token = canonicalToken.get(value) || value
  if (token.length > 5 && token.endsWith('ies')) return `${token.slice(0, -3)}y`
  if (token.length > 5 && token.endsWith('ing')) return token.slice(0, -3)
  if (token.length > 4 && token.endsWith('ed')) return token.slice(0, -2)
  if (token.length > 4 && token.endsWith('s')) return token.slice(0, -1)
  return token
}

export function retrievalTokens(value, { includeStopWords = false } = {}) {
  return baseNormalize(value)
    .split(/\s+/)
    .map(normalizeToken)
    .filter(token => token.length > 1 && (includeStopWords || !stopWords.has(token)))
}

export function detectedDomains(value) {
  const normalized = baseNormalize(value)
  const tokens = new Set(retrievalTokens(value, { includeStopWords: true }))
  return Object.entries(domainLexicon)
    .filter(([, terms]) => terms.some(term => tokens.has(term) || normalized.includes(term)))
    .map(([domain]) => domain)
}

export function expandedQueryTokens(value) {
  const original = retrievalTokens(value)
  const expanded = new Set(original)
  for (const domain of detectedDomains(value)) {
    for (const term of domainLexicon[domain] || []) expanded.add(normalizeToken(term))
  }
  return { original, expanded: [...expanded], domains: detectedDomains(value) }
}

function trigrams(value) {
  const normalized = `  ${baseNormalize(value)}  `
  const output = new Set()
  for (let index = 0; index < normalized.length - 2; index += 1) output.add(normalized.slice(index, index + 3))
  return output
}

export function trigramSimilarity(left, right) {
  const a = trigrams(left)
  const b = trigrams(right)
  if (!a.size || !b.size) return 0
  let intersection = 0
  for (const token of a) if (b.has(token)) intersection += 1
  return (2 * intersection) / (a.size + b.size)
}

export function rankKnowledgeChunks(question, chunks, limit = 12) {
  const query = expandedQueryTokens(question)
  const prepared = chunks.map(chunk => {
    const contentTokens = retrievalTokens(`${chunk.title} ${chunk.content}`)
    const frequencies = new Map()
    for (const token of contentTokens) frequencies.set(token, (frequencies.get(token) || 0) + 1)
    return {
      ...chunk,
      normalized: baseNormalize(`${chunk.title} ${chunk.content}`),
      titleTokens: new Set(retrievalTokens(chunk.title)),
      contentTokens,
      frequencies,
      domains: detectedDomains(`${chunk.title} ${chunk.content}`),
    }
  })
  const averageLength = prepared.reduce((sum, item) => sum + item.contentTokens.length, 0) / Math.max(1, prepared.length)
  const documentFrequency = new Map()
  for (const token of query.expanded) {
    documentFrequency.set(token, prepared.filter(item => item.frequencies.has(token)).length)
  }
  const original = new Set(query.original)
  const ranked = prepared.map(item => {
    let score = 0
    const matches = []
    for (const token of query.expanded) {
      const tf = item.frequencies.get(token) || 0
      if (!tf) continue
      const df = documentFrequency.get(token) || 0
      const idf = Math.log(1 + ((prepared.length - df + .5) / (df + .5)))
      const normalizedTf = (tf * 2.2) / (tf + 1.2 * (.25 + .75 * (item.contentTokens.length / Math.max(1, averageLength))))
      const weight = original.has(token) ? 2.25 : .55
      const titleBoost = item.titleTokens.has(token) ? 2.5 : 1
      score += idf * normalizedTf * weight * titleBoost
      matches.push(token)
    }
    const domainOverlap = query.domains.filter(domain => item.domains.includes(domain))
    score += domainOverlap.length * 5
    const compactQuestion = baseNormalize(question)
    if (compactQuestion.length > 8 && item.normalized.includes(compactQuestion)) score += 15
    for (const token of query.original.filter(value => value.length >= 5 && !matches.includes(value))) {
      const fuzzy = [...new Set(item.contentTokens)].reduce((best, candidate) => Math.max(best, trigramSimilarity(token, candidate)), 0)
      if (fuzzy >= .72) {
        score += fuzzy * 1.5
        matches.push(`${token}~`)
      }
    }
    return {
      source: item.source,
      title: item.title,
      index: item.index,
      content: item.content,
      score,
      matchedTerms: [...new Set(matches)].slice(0, 16),
      domains: domainOverlap,
    }
  }).filter(item => item.score > 0).sort((left, right) => right.score - left.score)

  const selected = []
  const perSource = new Map()
  for (const item of ranked) {
    if ((perSource.get(item.source) || 0) >= 3) continue
    selected.push(item)
    perSource.set(item.source, (perSource.get(item.source) || 0) + 1)
    if (selected.length >= limit) break
  }
  return selected
}

export function scoreCatalogTable(question, table) {
  const query = expandedQueryTokens(question)
  const rawTableName = String(table.name || '').toLowerCase()
  const tableName = baseNormalize(table.name)
  const columns = (table.columns || []).map(column => baseNormalize(column.name))
  const haystack = `${tableName} ${columns.join(' ')}`
  const haystackTokens = new Set(retrievalTokens(haystack))
  const original = new Set(query.original)
  let score = 0
  for (const token of query.expanded) {
    if (haystackTokens.has(token)) score += original.has(token) ? 9 : 2
    if (tableName.includes(token)) score += original.has(token) ? 10 : 3
    score += Math.min(6, columns.filter(column => column.includes(token)).length * (original.has(token) ? 2 : .5))
  }
  const tableDomains = detectedDomains(haystack)
  score += query.domains.filter(domain => tableDomains.includes(domain)).length * 5
  const essentialTables = {
    workforce: ['employees', 'clients'],
    payroll: ['payruns', 'payrunemployees', 'clients'],
    attendance: ['employee_monthly_attendance', 'clients'],
    leave: ['essleaverequests', 'employees', 'clients'],
    recruitment: ['recruitment_candidate_applications', 'recruitment_candidates', 'recruitment_application_scores', 'clients'],
    workflow: ['workflowtasks', 'workflowinstances', 'workflowmasters', 'workflowactivities'],
    travelExpense: ['ess_expense_claims', 'ess_travel_requests', 'clients'],
    attachment: ['attachment_storage_servers', 'entity_attachments'],
    billing: ['client_billing_configurations', 'client_billing_cost_rule_headers', 'client_billing_cost_rule_lines', 'clients'],
  }
  for (const domain of query.domains) {
    if ((essentialTables[domain] || []).includes(rawTableName)) score += 30
  }
  for (const token of query.original.filter(value => value.length >= 5)) {
    const fuzzy = Math.max(trigramSimilarity(token, tableName), ...columns.map(column => trigramSimilarity(token, column)))
    if (fuzzy >= .72) score += fuzzy * 3
  }
  if (['clients', 'employees'].includes(String(table.name).toLowerCase())) score += 1
  return score
}

export function relevantRelationshipHints(catalog = []) {
  const allowed = new Set(catalog.map(table => String(table.name).toLowerCase()))
  return relationshipRules.filter(rule => {
    const tables = [...rule.matchAll(/(?:^|->\s*)([A-Za-z0-9_]+)\./g)].map(match => match[1].toLowerCase())
    return tables.every(table => allowed.has(table))
  })
}

export function retrievalVersion() {
  return 'hybrid-bm25-domain-trigram-v3'
}
