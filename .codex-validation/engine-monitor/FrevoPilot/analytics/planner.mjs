const DEFAULT_MODEL = 'gemini-3.5-flash-lite'
const RETRYABLE_STATUS = new Set([408, 429, 500, 502, 503, 504])

const planningSchema = {
  type: 'object',
  properties: {
    canonicalQuestion: { type: 'string' },
    title: { type: 'string' },
    sql: { type: 'string' },
    parameters: { type: 'array', items: { type: 'string' } },
    chartType: { type: 'string', enum: ['auto', 'kpi', 'bar', 'line', 'doughnut', 'table'] },
    labelField: { type: 'string' },
    valueFields: { type: 'array', items: { type: 'string' } },
    explanation: { type: 'string' },
  },
  required: ['canonicalQuestion', 'title', 'sql', 'parameters', 'chartType', 'labelField', 'valueFields', 'explanation'],
}

const insightSchema = {
  type: 'object',
  properties: {
    summary: { type: 'string' },
    recommendations: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          severity: { type: 'string', enum: ['Low', 'Medium', 'High', 'Critical'] },
          title: { type: 'string' },
          narrative: { type: 'string' },
          confidence: { type: 'number' },
          evidence: { type: 'array', items: { type: 'string' } },
        },
        required: ['severity', 'title', 'narrative', 'confidence', 'evidence'],
      },
    },
    risks: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          severity: { type: 'string', enum: ['Low', 'Medium', 'High', 'Critical'] },
          title: { type: 'string' },
          narrative: { type: 'string' },
          confidence: { type: 'number' },
          evidence: { type: 'array', items: { type: 'string' } },
        },
        required: ['severity', 'title', 'narrative', 'confidence', 'evidence'],
      },
    },
  },
  required: ['summary', 'recommendations', 'risks'],
}

const intentSchema = {
  type: 'object',
  properties: {
    kind: { type: 'string', enum: ['conversation', 'clarification', 'analysis'] },
    reply: { type: 'string' },
    analyticsQuestion: { type: 'string' },
    clarificationDimension: { type: 'string', enum: ['none', 'client', 'period', 'metric', 'subject'] },
    presentationMode: { type: 'string', enum: ['none', 'single', 'dashboard'] },
    requiresPresentationChoice: { type: 'boolean' },
    choices: { type: 'array', items: { type: 'string' } },
  },
  required: ['kind', 'reply', 'analyticsQuestion', 'clarificationDimension', 'presentationMode', 'requiresPresentationChoice', 'choices'],
}

function apiConfiguration() {
  return {
    apiKey: (process.env.FREVOPILOT_API_KEY || process.env.GEMINI_API_KEY)?.trim(),
    model: (process.env.FREVOPILOT_MODEL || process.env.GEMINI_MODEL)?.trim() || DEFAULT_MODEL,
  }
}

function outputText(result) {
  if (typeof result?.output_text === 'string') return result.output_text
  if (typeof result?.text === 'string') return result.text
  if (typeof result?.text === 'function') return result.text()
  return ''
}

function retryable(error) {
  return RETRYABLE_STATUS.has(Number(error?.status || error?.code || error?.response?.status || 0))
}

async function callStructured(prompt, systemInstruction, schema) {
  if (globalThis.frevoPilotProvider) return globalThis.frevoPilotProvider(prompt, systemInstruction, schema)
  const { apiKey, model } = apiConfiguration()
  if (!apiKey) return null
  const { GoogleGenAI } = await import('@google/genai')
  const ai = new GoogleGenAI({ apiKey })
  let lastError
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      let result
      if (ai.interactions?.create) {
        result = await ai.interactions.create({
          model,
          input: prompt,
          system_instruction: systemInstruction,
          store: false,
          response_format: { type: 'text', mime_type: 'application/json', schema },
        }, { timeout: 60_000, maxRetries: 0 })
      } else {
        result = await ai.models.generateContent({
          model,
          contents: prompt,
          config: {
            httpOptions: { timeout: 60_000 },
            systemInstruction,
            responseMimeType: 'application/json',
            responseJsonSchema: schema,
          },
        })
      }
      return { payload: JSON.parse(outputText(result)), model }
    } catch (error) {
      lastError = error
      if (!retryable(error) || attempt === 2) break
      await new Promise(resolve => setTimeout(resolve, (2 ** attempt) * 1000))
    }
  }
  throw lastError || new Error('FrevoPilot analytics did not return a response.')
}

function tableExists(catalog, name) {
  return catalog.some(table => table.name.toLowerCase() === name.toLowerCase())
}

function deterministicIntent(message, clients = []) {
  const text = String(message || '').trim()
  const lower = text.toLowerCase()
  if (/^(?:hi|hii+|hello|hey|helo|namaste|good\s+(?:morning|afternoon|evening)|kaise ho|kya haal(?: hai)?)\W*$/i.test(text)) {
    return {
      kind: 'conversation',
      reply: 'Welcome! Main FrevoPilot hoon. Aap payroll, workforce, attendance, leave, recruitment, billing ya kisi aur HRMS area par sawal pooch sakte hain. Main zarurat padne par scope confirm karke single insight ya full executive dashboard banaunga.',
      analyticsQuestion: '', clarificationDimension: 'none', presentationMode: 'none', requiresPresentationChoice: false, choices: [],
    }
  }
  if (/^(?:thanks?|thank you|shukriya|ok|okay|acha|achha|theek hai|thik hai)\W*$/i.test(text)) {
    return {
      kind: 'conversation', reply: 'You are welcome. Agla business question batayiye, ya current analysis ko single insight ya full dashboard me dekhne ko kahiye.',
      analyticsQuestion: '', clarificationDimension: 'none', presentationMode: 'none', requiresPresentationChoice: false, choices: [],
    }
  }
  const clientScopedMaster = /\b(?:leave\s*types?|holidays?|attendance\s*polic(?:y|ies)|salary\s*templates?|work\s*locations?|geo[- ]?fenc(?:e|ing)|travel\s*(?:and|&)\s*expense\s*polic(?:y|ies)|benefit\s*templates?)\b/i.test(text)
  const explicitOrganizationScope = /\b(?:all\s+clients?|(?:by|across|per)\s+(?:all\s+)?clients?|client[- ]?wise|entire\s+(?:system|organization|organisation)|whole\s+(?:system|organization|organisation)|pure\s+system|organization[- ]?wide|organisation[- ]?wide)\b/i.test(text)
  const normalized = lower.replace(/[^a-z0-9]+/g, ' ')
  const explicitClient = clients.some(client => {
    const code = String(client.code || '').toLowerCase().trim()
    const name = String(client.name || '').toLowerCase().trim()
    return (code && new RegExp(`\\b${code.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\b`, 'i').test(normalized)) || (name && lower.includes(name))
  })
  if (clientScopedMaster && !explicitOrganizationScope && !explicitClient) {
    const choices = clients.slice(0, 8).map(client => client.code).filter(Boolean).join(', ')
    return {
      kind: 'clarification',
      reply: `Ye configuration client-wise hai. Aap pure system ke records dekhna chahte hain ya kisi specific client ke?${choices ? ` Available clients: ${choices}.` : ''}`,
      analyticsQuestion: text, clarificationDimension: 'client', presentationMode: 'none', requiresPresentationChoice: false,
      choices: ['All clients', ...clients.slice(0, 8).map(client => client.code).filter(Boolean)],
    }
  }
  const analytical = /\b(?:show|give|list|compare|count|total|highest|lowest|trend|risk|report|analytics?|dashboard|how many|kitna|kitne|dikhao|dikhaiye|batao|btado|payroll|headcount|workforce|attendance|recruitment|candidate|workflow|travel|expense|attachment|storage)\b/i.test(lower)
  if (analytical) {
    const dashboard = /\b(?:full|executive|admin|multi[- ]?chart|multi[- ]?view)\s+dashboard\b|\bdashboard\s+(?:bana|bna|make|create|generate|view)\b/i.test(lower)
    const single = /\b(?:focused\s+)?(?:single|one)\s+(?:insight|chart|visual)|\b(?:bar|line|doughnut|pie)\s+(?:chart|graph)\b/i.test(lower)
    const mode = dashboard ? 'dashboard' : single ? 'single' : 'none'
    return {
      kind: 'analysis',
      reply: mode === 'dashboard'
        ? 'Main isse KPI cards, multiple charts aur supporting table wale responsive executive dashboard me banaunga.'
        : mode === 'single'
          ? 'Main isse focused single decision insight me banaunga.'
          : 'Question clear hai. Aap focused single insight chahte hain ya cards, multiple charts aur table wala full executive dashboard?',
      analyticsQuestion: text,
      clarificationDimension: 'none',
      presentationMode: mode,
      requiresPresentationChoice: mode === 'none',
      choices: mode === 'none' ? ['Single insight', 'Full executive dashboard'] : [],
    }
  }
  return null
}

export async function interpretAnalyticsMessage({ message, conversation = [], currentQuestion = '', clients = [] }) {
  const deterministic = deterministicIntent(message, clients)
  if (deterministic) return deterministic
  const lower = String(message || '').toLowerCase()
  const explicitDashboard = /\b(?:full|executive|admin|multi[- ]?chart|multi[- ]?view)\s+dashboard\b|\bdashboard\s+(?:bana|bna|make|create|generate|view)\b/i.test(lower)
  const explicitSingle = /\b(?:single|one)\s+(?:insight|chart|visual)|\b(?:bar|line|doughnut|pie)\s+(?:chart|graph)\b/i.test(lower)
  const prompt = JSON.stringify({
    message,
    currentAnalysisQuestion: currentQuestion || null,
    recentConversation: conversation.slice(-6),
    activeClients: clients.map(client => ({ code: client.code, name: client.name })),
  })
  const system = [
    'You are FrevoPilot, a conversational executive analytics assistant for a client-wise HRMS.',
    'Do not generate SQL or a dashboard in this step. Classify whether the message is casual conversation, needs one clarification, or is ready for analysis.',
    'Greet naturally. For client-wise masters such as leave types, holidays, attendance policies, salary templates, work locations and geo-fences, ask whether the user means the whole organization or a specific client when missing.',
    'Ask only one concise cross-question at a time. Preserve the user language and tone.',
    'If the user explicitly requests a full/admin/executive dashboard, choose dashboard. If a specific single chart is requested, choose single.',
    'For a ready analytical request with no presentation specified, require a presentation choice instead of assuming a bar chart.',
    'Return only schema-valid JSON.',
  ].join('\n')
  try {
    const response = await callStructured(prompt, system, intentSchema)
    if (response) {
      const result = response.payload
      if (explicitDashboard) result.presentationMode = 'dashboard'
      if (explicitSingle) result.presentationMode = 'single'
      if (['dashboard', 'single'].includes(result.presentationMode)) result.requiresPresentationChoice = false
      return {
        kind: result.kind,
        reply: String(result.reply || '').slice(0, 1500),
        analyticsQuestion: String(result.analyticsQuestion || message).slice(0, 4000),
        clarificationDimension: result.clarificationDimension || 'none',
        presentationMode: result.presentationMode || 'none',
        requiresPresentationChoice: Boolean(result.requiresPresentationChoice),
        choices: Array.isArray(result.choices) ? result.choices.filter(Boolean).slice(0, 10) : [],
      }
    }
  } catch {
    // Use the safe local interpretation below when the model is unavailable.
  }
  const analytical = /\b(?:show|give|list|compare|count|total|highest|lowest|trend|risk|report|analytics?|dashboard|how many|kitna|kitne|dikhao|batao|btado)\b/i.test(lower)
  if (!analytical) {
    return {
      kind: 'conversation',
      reply: 'Main aapki HRMS analytics me help kar sakta hoon. Aap jis decision, metric, client ya period ko samajhna chahte hain wo batayiye.',
      analyticsQuestion: '', clarificationDimension: 'none', presentationMode: 'none', requiresPresentationChoice: false, choices: [],
    }
  }
  const mode = explicitDashboard ? 'dashboard' : explicitSingle ? 'single' : 'none'
  return {
    kind: 'analysis',
    reply: mode === 'dashboard' ? 'Main isse responsive full executive dashboard ke roop me banaunga.' : mode === 'single' ? 'Main isse focused single insight ke roop me banaunga.' : 'Question clear hai. Aap focused single insight chahte hain ya cards, multiple charts aur table wala full executive dashboard?',
    analyticsQuestion: String(message).slice(0, 4000), clarificationDimension: 'none', presentationMode: mode,
    requiresPresentationChoice: mode === 'none', choices: mode === 'none' ? ['Single insight', 'Full executive dashboard'] : [],
  }
}

function fallbackPlan(question, catalog, sourceDatabase) {
  const lower = String(question).toLowerCase()
  const db = `\`${sourceDatabase}\``
  if (/ats|resume\s*(?:match|score)|candidate\s*score|scoring/.test(lower) && tableExists(catalog, 'recruitment_application_scores')) {
    return {
      canonicalQuestion: 'Current ATS score quality by client and recommendation',
      title: 'Current ATS score quality',
      sql: `SELECT c.Code AS client_code, COALESCE(s.recommendation,'Unclassified') AS recommendation, ROUND(AVG(s.TotalScore),2) AS average_score, COUNT(DISTINCT s.ApplicationId) AS scored_applications, SUM(CASE WHEN s.humanreviewrequired=1 THEN 1 ELSE 0 END) AS human_review_required FROM ${db}.recruitment_application_scores s INNER JOIN ${db}.recruitment_candidate_applications a ON a.Id=s.ApplicationId INNER JOIN ${db}.clients c ON c.Id=a.ClientId WHERE s.IsCurrent=1 GROUP BY c.Code, COALESCE(s.recommendation,'Unclassified') ORDER BY average_score DESC, c.Code`,
      parameters: [], chartType: 'bar', labelField: 'recommendation', valueFields: ['average_score', 'scored_applications'],
      explanation: 'Deterministic current-score analysis using one score row per application and the canonical application-to-client relationship.', model: 'FrevoPilot deterministic',
    }
  }
  if (/workflow|approval|approver|bottleneck/.test(lower) && tableExists(catalog, 'workflowtasks')) {
    const pendingOnly = /\bpending\b/.test(lower)
    return {
      canonicalQuestion: pendingOnly ? 'Pending workflow approval tasks by activity' : 'Workflow approval tasks by activity and status',
      title: pendingOnly ? 'Pending approval bottlenecks' : 'Workflow task status',
      sql: `SELECT wm.Code AS workflow_code, COALESCE(act.DisplayName,wm.Name) AS activity_name, t.Status AS task_status, COUNT(DISTINCT t.Id) AS task_count FROM ${db}.workflowtasks t INNER JOIN ${db}.workflowinstances i ON i.Id=t.InstanceId INNER JOIN ${db}.workflowmasters wm ON wm.Id=i.WorkflowId LEFT JOIN ${db}.workflowactivities act ON act.ActivityCode=wm.Code${pendingOnly ? ` WHERE t.Status='Pending'` : ''} GROUP BY wm.Code, COALESCE(act.DisplayName,wm.Name), t.Status ORDER BY task_count DESC, wm.Code`,
      parameters: [], chartType: 'bar', labelField: 'activity_name', valueFields: ['task_count'],
      explanation: 'Deterministic workflow plan using the canonical task-to-instance-to-master-to-activity relationship and distinct task counting.', model: 'FrevoPilot deterministic',
    }
  }
  if (/expense|claim|reimbursement/.test(lower) && tableExists(catalog, 'ess_expense_claims')) {
    return {
      canonicalQuestion: 'Expense claim submitted and approved amounts by client and status',
      title: 'Expense claim value',
      sql: `SELECT c.Code AS client_code, e.Status AS claim_status, COUNT(e.Id) AS claim_count, ROUND(SUM(e.TotalClaimAmount),2) AS claimed_amount, ROUND(SUM(e.TotalApprovedAmount),2) AS approved_amount FROM ${db}.ess_expense_claims e INNER JOIN ${db}.clients c ON c.Id=e.ClientId GROUP BY c.Code, e.Status ORDER BY claimed_amount DESC, c.Code`,
      parameters: [], chartType: 'bar', labelField: 'claim_status', valueFields: ['claimed_amount', 'approved_amount'],
      explanation: 'Deterministic expense analysis separating submitted and approved claim value.', model: 'FrevoPilot deterministic',
    }
  }
  if (/travel|trip/.test(lower) && tableExists(catalog, 'ess_travel_requests')) {
    return {
      canonicalQuestion: 'Travel request volume and estimated cost by client and status',
      title: 'Travel request status',
      sql: `SELECT c.Code AS client_code, t.Status AS request_status, COUNT(t.Id) AS request_count, ROUND(SUM(t.EstimatedCost),2) AS estimated_cost FROM ${db}.ess_travel_requests t INNER JOIN ${db}.clients c ON c.Id=t.ClientId GROUP BY c.Code, t.Status ORDER BY estimated_cost DESC, c.Code`,
      parameters: [], chartType: 'bar', labelField: 'request_status', valueFields: ['request_count', 'estimated_cost'],
      explanation: 'Deterministic travel analysis using recorded request estimates.', model: 'FrevoPilot deterministic',
    }
  }
  if (/attachment|storage|mount|google\s*drive/.test(lower) && tableExists(catalog, 'attachment_storage_servers')) {
    return {
      canonicalQuestion: 'Attachment storage mount availability and health',
      title: 'Attachment storage health',
      sql: `SELECT server_code, storage_type, CASE WHEN is_default_write_server=1 THEN 'Default write' WHEN is_write_enabled=1 THEN 'Write enabled' ELSE 'Read only' END AS mount_role, COALESCE(last_health_check_status,'Not checked') AS health_status, COUNT(*) AS server_count, MAX(is_default_write_server) AS default_write, MIN(priority) AS mount_priority FROM ${db}.attachment_storage_servers WHERE is_active=1 AND (is_read_enabled=1 OR is_write_enabled=1) GROUP BY server_code, storage_type, mount_role, COALESCE(last_health_check_status,'Not checked') ORDER BY default_write DESC, mount_priority, server_code`,
      parameters: [], chartType: 'bar', labelField: 'server_code', valueFields: ['server_count'],
      explanation: 'Deterministic storage analysis using active read/write flags, default-write selection and the latest health status.', model: 'FrevoPilot deterministic',
    }
  }
  if (/salary\s*component|pay\s*component|earning|deduction/.test(lower) && tableExists(catalog, 'salarycomponents')) {
    return {
      canonicalQuestion: 'Active salary components by type and role',
      title: 'Salary component configuration',
      sql: `SELECT ComponentType AS component_type, COALESCE(ComponentRole,'Unspecified') AS component_role, COUNT(Id) AS component_count, SUM(CASE WHEN Taxable=1 THEN 1 ELSE 0 END) AS taxable_components FROM ${db}.salarycomponents WHERE Active=1 GROUP BY ComponentType, COALESCE(ComponentRole,'Unspecified') ORDER BY component_count DESC`,
      parameters: [], chartType: 'bar', labelField: 'component_role', valueFields: ['component_count', 'taxable_components'],
      explanation: 'Deterministic salary component analysis using active configuration rows.', model: 'FrevoPilot deterministic',
    }
  }
  if (/billing|rate\s*card|gst|cost\s*rule/.test(lower) && tableExists(catalog, 'client_billing_configurations')) {
    return {
      canonicalQuestion: 'Active client billing rate and cost-rule configuration',
      title: 'Client billing configuration',
      sql: `SELECT c.Code AS client_code, COALESCE(cfg.active_configurations,0) AS active_configurations, COALESCE(cfg.average_configured_value,0) AS average_configured_value, COALESCE(rules.active_rule_headers,0) AS active_rule_headers, COALESCE(rules.active_rule_lines,0) AS active_rule_lines FROM ${db}.clients c LEFT JOIN (SELECT ClientId, COUNT(Id) AS active_configurations, ROUND(AVG(Value),2) AS average_configured_value FROM ${db}.client_billing_configurations WHERE IsActive=1 GROUP BY ClientId) cfg ON cfg.ClientId=c.Id LEFT JOIN (SELECT h.ClientId, COUNT(DISTINCT h.Id) AS active_rule_headers, COUNT(l.Id) AS active_rule_lines FROM ${db}.client_billing_cost_rule_headers h LEFT JOIN ${db}.client_billing_cost_rule_lines l ON l.HeaderId=h.Id AND l.IsActive=1 WHERE h.IsActive=1 GROUP BY h.ClientId) rules ON rules.ClientId=c.Id WHERE c.IsActive=1 AND (cfg.active_configurations IS NOT NULL OR rules.active_rule_headers IS NOT NULL) ORDER BY active_configurations DESC, c.Code`,
      parameters: [], chartType: 'bar', labelField: 'client_code', valueFields: ['active_configurations', 'active_rule_headers', 'active_rule_lines'],
      explanation: 'Deterministic configuration report with each one-to-many source pre-aggregated before joining, preventing fanout. Values are configuration, not realized revenue.', model: 'FrevoPilot deterministic',
    }
  }
  if (/attendance|present|absent|payable|lop/.test(lower) && tableExists(catalog, 'employee_monthly_attendance')) {
    return {
      canonicalQuestion: 'Monthly attendance status and payability by client',
      title: 'Attendance and payability overview',
      sql: `SELECT c.Code AS client_code, a.attendance_month, ROUND(SUM(a.present_days),2) AS present_days, ROUND(SUM(a.payable_days),2) AS payable_days, ROUND(SUM(a.lop_days),2) AS lop_days FROM ${db}.employee_monthly_attendance a INNER JOIN ${db}.clients c ON c.Id=a.client_id GROUP BY c.Code, a.attendance_month ORDER BY a.attendance_month DESC, c.Code`,
      parameters: [], chartType: 'bar', labelField: 'client_code', valueFields: ['present_days', 'payable_days', 'lop_days'],
      explanation: 'Deterministic fallback using monthly attendance and client masters.', model: 'FrevoPilot deterministic',
    }
  }
  if (/payroll|salary|net pay|netpay|cost|wage|ctc/.test(lower) && tableExists(catalog, 'payruns')) {
    return {
      canonicalQuestion: 'Payroll cost and net pay trend by client and period',
      title: 'Payroll cost trend',
      sql: `SELECT c.Code AS client_code, p.PayPeriod AS pay_period, ROUND(SUM(p.PayrollCost),2) AS payroll_cost, ROUND(SUM(p.NetPay),2) AS net_pay, COUNT(*) AS run_count FROM ${db}.payruns p INNER JOIN ${db}.clients c ON c.Id=p.ClientId GROUP BY c.Code, p.PayPeriod ORDER BY p.PayPeriod DESC, c.Code`,
      parameters: [], chartType: 'line', labelField: 'pay_period', valueFields: ['payroll_cost', 'net_pay'],
      explanation: 'Deterministic fallback using completed payroll run totals.', model: 'FrevoPilot deterministic',
    }
  }
  if (/recruit|candidate|ats|resume|offer|hiring/.test(lower) && tableExists(catalog, 'recruitment_candidate_applications')) {
    return {
      canonicalQuestion: 'Recruitment application funnel by client and stage',
      title: 'Recruitment funnel',
      sql: `SELECT c.Code AS client_code, a.CurrentStage AS stage, COUNT(*) AS applications FROM ${db}.recruitment_candidate_applications a INNER JOIN ${db}.clients c ON c.Id=a.ClientId GROUP BY c.Code, a.CurrentStage ORDER BY applications DESC`,
      parameters: [], chartType: 'bar', labelField: 'stage', valueFields: ['applications'],
      explanation: 'Deterministic fallback using candidate applications and current stage.', model: 'FrevoPilot deterministic',
    }
  }
  if (/leave/.test(lower) && tableExists(catalog, 'essleaverequests')) {
    return {
      canonicalQuestion: 'Leave requests by client and status',
      title: 'Leave request status',
      sql: `SELECT c.Code AS client_code, l.Status AS status, COUNT(*) AS request_count, ROUND(SUM(l.Days),2) AS leave_days FROM ${db}.essleaverequests l INNER JOIN ${db}.clients c ON c.Id=l.ClientId GROUP BY c.Code, l.Status ORDER BY request_count DESC`,
      parameters: [], chartType: 'doughnut', labelField: 'status', valueFields: ['request_count'],
      explanation: 'Deterministic fallback using ESS leave requests.', model: 'FrevoPilot deterministic',
    }
  }
  return {
    canonicalQuestion: 'Active employee headcount by client',
    title: 'Active workforce by client',
    sql: `SELECT c.Code AS client_code, c.Name AS client_name, COUNT(e.Id) AS active_employees FROM ${db}.clients c LEFT JOIN ${db}.employees e ON e.ClientId=c.Id AND e.IsActive=1 WHERE c.IsActive=1 GROUP BY c.Id, c.Code, c.Name ORDER BY active_employees DESC`,
    parameters: [], chartType: 'bar', labelField: 'client_code', valueFields: ['active_employees'],
    explanation: 'Deterministic fallback using the client and employee masters.', model: 'FrevoPilot deterministic',
  }
}

export function enforceSemanticGuards(plan, question, catalog, sourceDatabase) {
  const sql = String(plan?.sql || '')
  const lowerQuestion = String(question || '').toLowerCase()
  if (/workflow|approval|approver|bottleneck/.test(lowerQuestion)
    && /workflowactivities/i.test(sql)
    && !/(?:[A-Za-z0-9_]+\.)?ActivityCode\s*=\s*(?:[A-Za-z0-9_]+\.)?Code|(?:[A-Za-z0-9_]+\.)?Code\s*=\s*(?:[A-Za-z0-9_]+\.)?ActivityCode/i.test(sql)) {
    return fallbackPlan(question, catalog, sourceDatabase)
  }
  if (/ats|resume\s*(?:match|score)|candidate\s*score|scoring/.test(lowerQuestion)
    && /recruitment_application_scores/i.test(sql)
    && !/\bIsCurrent\b/i.test(sql)) {
    return fallbackPlan(question, catalog, sourceDatabase)
  }
  if (/(?:active\s+)?employee|headcount|workforce/.test(lowerQuestion)
    && /client/.test(lowerQuestion)
    && !/\bclient_(?:code|name)\b/i.test(sql)) {
    return fallbackPlan(question, catalog, sourceDatabase)
  }
  return plan
}

export async function createQueryPlan({ question, catalog, knowledge, semanticContext, sourceDatabase }) {
  const exposedCatalog = catalog.map(table => ({
    table: table.name,
    rowEstimate: table.rowEstimate,
    columns: table.columns.filter(column => !column.sensitive).map(column => `${column.name}:${column.type}`),
  }))
  const prompt = JSON.stringify({
    userQuestion: question,
    mysqlDatabase: sourceDatabase,
    relevantSchema: exposedCatalog,
    approvedMetricsAndTerminology: semanticContext,
    hrmsBusinessKnowledge: knowledge,
  })
  const system = [
    'You are the governed analytics planner inside FrevoPilot for a MySQL 8 HRMS database.',
    'Translate the CEO question into one accurate aggregate SELECT query using only supplied tables and non-sensitive columns.',
    'Never use employee names, email, mobile, PAN, Aadhaar, bank, address, password, token, resume text, file path or other PII.',
    'Use explicit database-qualified table names and aliases. Never use information_schema or any unsupplied table.',
    'Do not use CTEs, variables, comments, semicolons, temporary tables, stored procedures, DDL or DML.',
    'Prefer verified business totals already stored in payruns/payrunemployees and normalized attendance/recruitment tables.',
    'Treat supplied relationship hints and business rules as mandatory. For workflow tasks use task InstanceId -> instance Id -> workflow master Id -> activity ActivityCode; never join workflow activity by ResourceType alone.',
    'Prevent aggregate fanout: pre-aggregate each one-to-many child in a derived table before combining multiple children, and count business records distinctly where appropriate.',
    'For a current ATS score analysis filter recruitment_application_scores.IsCurrent=1.',
    'Do not describe billing configurations or cost-rule rates as realized revenue or invoices.',
    'Use ? placeholders for user-supplied literal filters and return their values in exact order as strings.',
    'Do not invent a period or client when the question does not provide one; return an organization-wide current overview instead.',
    'Do not add implicit status filters. Active employees means employees.IsActive=1, not clients.IsActive=1. Include inactive clients with matching records unless the user explicitly requests active clients.',
    'Keep detail bounded and executive-friendly. The execution layer will enforce a final row limit.',
    'Choose a chart type and exact output field aliases. Return only schema-valid JSON.',
  ].join('\n')
  try {
    const response = await callStructured(prompt, system, planningSchema)
    if (!response) {
      if (globalThis.frevoPilotProvider) throw new Error('The configured AI providers are temporarily unavailable. Check AI Integrations and retry after the provider reset window.')
      return fallbackPlan(question, catalog, sourceDatabase)
    }
    const plan = response.payload
    return enforceSemanticGuards({
      canonicalQuestion: String(plan.canonicalQuestion || question).slice(0, 1000),
      title: String(plan.title || 'FrevoPilot executive analysis').slice(0, 255),
      sql: String(plan.sql || '').trim(),
      parameters: Array.isArray(plan.parameters) ? plan.parameters.slice(0, 20).map(value => String(value)) : [],
      chartType: plan.chartType || 'auto',
      labelField: String(plan.labelField || '').slice(0, 190),
      valueFields: Array.isArray(plan.valueFields) ? plan.valueFields.slice(0, 8).map(value => String(value).slice(0, 190)) : [],
      explanation: String(plan.explanation || '').slice(0, 1200),
      model: response.model,
      providerCode: response.providerCode,
      simpleCountContract: response.simpleCountContract,
    }, question, catalog, sourceDatabase)
  } catch (error) {
    if (globalThis.frevoPilotProvider) throw error
    return fallbackPlan(question, catalog, sourceDatabase)
  }
}

export async function repairQueryPlan({ question, failedPlan, validationError, catalog, knowledge, semanticContext, sourceDatabase }) {
  const exposedCatalog = catalog.map(table => ({
    table: table.name,
    columns: table.columns.filter(column => !column.sensitive).map(column => `${column.name}:${column.type}`),
  }))
  const prompt = JSON.stringify({
    userQuestion: question,
    mysqlDatabase: sourceDatabase,
    failedPlan: {
      canonicalQuestion: failedPlan.canonicalQuestion,
      sql: failedPlan.sql,
      parameters: failedPlan.parameters || [],
      chartType: failedPlan.chartType,
      labelField: failedPlan.labelField,
      valueFields: failedPlan.valueFields || [],
    },
    validationError: String(validationError?.message || validationError).slice(0, 1500),
    relevantSchema: exposedCatalog,
    approvedMetricsAndTerminology: semanticContext,
    hrmsBusinessKnowledge: knowledge,
  })
  const system = [
    'You are the SQL repair controller inside FrevoPilot for a MySQL 8 HRMS analytics engine.',
    'Correct the failed aggregate SELECT plan using only the supplied real schema and the validation error.',
    'Never add DML, DDL, CTEs, comments, semicolons, variables, temporary tables, procedures, PII or unsupplied tables/columns.',
    'Preserve the business meaning of the user question. Do not substitute an unrelated workforce query.',
    'Do not add implicit status filters, including clients.IsActive=1 unless active clients were explicitly requested.',
    'Honor supplied relationships exactly, pre-aggregate one-to-many child sources to avoid fanout, and use IsCurrent=1 for current ATS scores.',
    'For workflow activity use workflowtasks.InstanceId -> workflowinstances.Id -> workflowmasters.Id -> workflowactivities.ActivityCode; ResourceType alone is not a valid activity join.',
    'Use database-qualified tables and exact output aliases that match labelField and valueFields.',
    'Return only schema-valid JSON.',
  ].join('\n')
  const response = await callStructured(prompt, system, planningSchema)
  if (!response) throw new Error(`The generated query failed validation and FrevoPilot repair is unavailable: ${validationError?.message || validationError}`)
  const plan = response.payload
  return enforceSemanticGuards({
    canonicalQuestion: String(plan.canonicalQuestion || failedPlan.canonicalQuestion || question).slice(0, 1000),
    title: String(plan.title || failedPlan.title || 'FrevoPilot executive analysis').slice(0, 255),
    sql: String(plan.sql || '').trim(),
    parameters: Array.isArray(plan.parameters) ? plan.parameters.slice(0, 20).map(value => String(value)) : [],
    chartType: plan.chartType || failedPlan.chartType || 'auto',
    labelField: String(plan.labelField || '').slice(0, 190),
    valueFields: Array.isArray(plan.valueFields) ? plan.valueFields.slice(0, 8).map(value => String(value).slice(0, 190)) : [],
    explanation: String(plan.explanation || `Self-repaired after ${validationError?.message || validationError}`).slice(0, 1200),
    model: response.model,
    providerCode: failedPlan.providerCode ?? response.providerCode,
    // Repair prompts may use the full catalog, but cannot erase the original
    // host contract. Model payload fields are deliberately never consulted.
    simpleCountContract: failedPlan.simpleCountContract ?? response.simpleCountContract,
  }, question, catalog, sourceDatabase)
}

function firstEvidence(rows) {
  const row = rows.find(item => item && typeof item === 'object') || {}
  const evidence = Object.entries(row).slice(0, 3).map(([key, value]) => `${key}=${value ?? 'null'}`)
  return evidence.length ? evidence : ['row_count=0']
}

function fallbackInsights(question, rows, chart) {
  const evidence = firstEvidence(rows)
  const numeric = rows.flatMap(row => Object.entries(row).filter(([, value]) => typeof value === 'number'))
  const best = numeric.sort((a, b) => Number(b[1]) - Number(a[1]))[0]
  return {
    summary: rows.length
      ? `FrevoPilot analysed ${rows.length} result row${rows.length === 1 ? '' : 's'} for “${question}”. The dashboard reflects the current local HRMS data snapshot.`
      : `No matching HRMS data was found for “${question}” in the current scope.`,
    insights: [
      {
        type: 'Recommendation', severity: rows.length ? 'Medium' : 'Low',
        title: rows.length ? 'Review the leading segment' : 'Validate source coverage',
        narrative: rows.length
          ? `Use the ${chart.type} view and its backing table to review the highest-value segment before making an operational decision.`
          : 'Confirm the requested client, period and source configuration before acting.',
        confidence: rows.length ? 0.76 : 0.9,
        evidence: best ? [`${best[0]}=${best[1]}`, ...evidence] : evidence,
      },
      {
        type: 'Risk', severity: rows.length ? 'Low' : 'Medium',
        title: rows.length ? 'Snapshot and data-quality risk' : 'Missing-data risk',
        narrative: rows.length
          ? 'This is a point-in-time analytical snapshot. Confirm freshness and investigate null or incomplete source records before committing a financial or people decision.'
          : 'An empty result can hide missing configuration or incomplete transactions; do not interpret it as zero business exposure.',
        confidence: 0.82,
        evidence: evidence.length ? evidence : ['row_count=0'],
      },
    ],
  }
}

function localDatabaseSummary(rows) {
  return {
    summary: rows.length
      ? `The live HRMS query returned ${rows.length} result group${rows.length === 1 ? '' : 's'}. The chart and table show the returned values; no AI interpretation was generated.`
      : 'The live HRMS query returned no matching records in this scope. This does not establish zero activity or complete source coverage.',
    insights: [],
    summarySource: 'database-only',
    summaryWarning: 'AI summary unavailable; live database totals are shown',
  }
}

export async function createExecutiveInsights({ question, rows, chart, knowledge, localProvider = false }) {
  const safeRows = rows.slice(0, 80)
  const prompt = JSON.stringify({ question, resultRows: safeRows, rowCount: rows.length, rowsTruncated: rows.length > safeRows.length, metricFields: chart.valueFields, supportingBusinessKnowledge: knowledge.slice(0, 5) })
  const system = [
    'You are FrevoPilot Executive Analytics. Produce a concise CEO summary, recommendations and risks from only the supplied result rows.',
    'Every recommendation and risk must cite concrete evidence copied from the supplied rows. Never invent a number, trend, cause or forecast.',
    'The summary must describe observed metrics only, not speculate. An absent Unspecified/unmapped group is not evidence of a data gap. Do not add rows or categories that were not returned.',
    'Suggestions are optional and must be identified as suggestions, not proven problems. Return empty recommendations/risks arrays when the data does not justify them. If rowsTruncated=true, describe only the shown rows and never claim their totals cover the whole result.',
    'If evidence is insufficient, state that clearly. Keep recommendations actionable and risks decision-oriented.',
    'Confidence is 0 to 1. Return zero to three recommendations and zero to three risks. Return only schema-valid JSON.',
  ].join('\n')
  try {
    const response = await callStructured(prompt, system, insightSchema)
    if (!response) return localProvider ? localDatabaseSummary(rows) : fallbackInsights(question, rows, chart)
    if (localProvider && (typeof response.payload?.summary !== 'string' || !response.payload.summary.trim())) return localDatabaseSummary(rows)
    const defaultEvidence = firstEvidence(rows)
    const normalize = (item, type) => ({
      type,
      severity: item.severity,
      title: String(item.title).slice(0, 255),
      narrative: String(item.narrative).slice(0, 2000),
      confidence: Math.max(0, Math.min(1, Number(item.confidence) || 0)),
      evidence: (item.evidence || []).length
        ? item.evidence.map(value => String(value).slice(0, 500)).slice(0, 8)
        : defaultEvidence,
    })
    const recommendations = (response.payload.recommendations || []).map(item => normalize(item, 'Recommendation'))
    const risks = (response.payload.risks || []).map(item => normalize(item, 'Risk'))
    return {
      summary: String(response.payload.summary || '').slice(0, 3000),
      insights: [...recommendations, ...risks],
      ...(localProvider ? { summarySource: 'ai' } : {}),
    }
  } catch {
    return localProvider ? localDatabaseSummary(rows) : fallbackInsights(question, rows, chart)
  }
}

export function analyticsModelStatus() {
  const { apiKey, model } = apiConfiguration()
  return { enabled: Boolean(apiKey), provider: 'FrevoPilot', model, fallback: 'Schema-aware deterministic analytics' }
}
