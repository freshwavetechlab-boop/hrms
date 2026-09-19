import parserPackage from 'node-sql-parser'
import { analyticsLimits } from './config.mjs'

const { Parser } = parserPackage
const parser = new Parser()

const forbidden = /(;|--|\/\*|#\s|\b(?:insert|update|delete|replace|merge|alter|create|drop|truncate|rename|grant|revoke|call|execute|handler|load_file|outfile|dumpfile|sleep|benchmark|information_schema|performance_schema|mysql\.)\b)/i
const sensitive = /(password|secret|token|apikey|cipher|credential|aadhaar|aadhar|pannumber|bankaccount|accountno|ifsccode|mobile|phone|email|address|parsedtext|selfie|filepath|firstname|lastname|fullname|dateofbirth|json|employeecode|candidatecode|applicationcode|offernumber|overallfeedback)/i

function cleanSql(value) {
  return String(value || '')
    .replace(/^```(?:sql)?\s*/i, '')
    .replace(/\s*```$/i, '')
    .trim()
}

function normalizeTableList(list = []) {
  return list.map(entry => {
    const [, database, table] = String(entry).split('::')
    return { database: !database || database === 'null' ? null : database, table }
  })
}

export function validateAnalyticsSql(input, { sourceDatabase, allowedTables, allowSensitive = false } = {}) {
  let sql = cleanSql(input)
  if (!sql) throw new Error('FrevoPilot did not produce a query.')
  if (forbidden.test(sql)) throw new Error('The analytics query contains a blocked SQL operation.')
  let ast
  let tables
  let columns
  try {
    ast = parser.astify(sql, { database: 'mysql' })
    tables = normalizeTableList(parser.tableList(sql, { database: 'mysql' }))
    columns = parser.columnList(sql, { database: 'mysql' })
  } catch (error) {
    throw new Error(`The analytics query could not be validated: ${error.message}`)
  }
  if (Array.isArray(ast) || ast?.type !== 'select') throw new Error('Only one SELECT query is allowed.')
  const allowedFunctions = new Set(['COUNT', 'SUM', 'AVG', 'MIN', 'MAX', 'COALESCE', 'IFNULL', 'NULLIF', 'ROUND', 'DATE', 'DATE_FORMAT', 'TIMESTAMPDIFF', 'DATEDIFF', 'YEAR', 'MONTH', 'DAY', 'CONCAT', 'CONCAT_WS', 'LOWER', 'UPPER', 'TRIM', 'GREATEST', 'LEAST', 'CAST', 'CONVERT', 'ABS', 'IF', 'CURDATE', 'NOW', 'UTC_TIMESTAMP'])
  function check(node, countArgument = false) {
    if (!node || typeof node !== 'object') return
    if (Array.isArray(node)) { node.forEach(child => check(child, countArgument)); return }
    if (node.type === 'select' && (node.into?.position || node.locking_read)) throw new Error('Locking/export statements are blocked.')
    if (node.type === 'select' && (node._next || node.with)) throw new Error('Use one SELECT without UNION or CTEs.')
    if (node.type === 'column_ref' && node.column === '*' && !countArgument) throw new Error('Select explicit non-sensitive columns; SELECT * is blocked.')
    if (node.type === 'function' || node.type === 'aggr_func') {
      const name = typeof node.name === 'string' ? node.name : node.name?.name?.map(part => part.value).join('.')
      if (!allowedFunctions.has(String(name).toUpperCase())) throw new Error('This SQL function is not approved for analytics.')
      countArgument = String(name).toUpperCase() === 'COUNT'
    }
    for (const child of Object.values(node)) check(child, countArgument)
  }
  check(ast)
  if (/\binto\b|\bfor\s+update\b|\block\s+in\b|@|:=/i.test(sql)) throw new Error('Export, locking and variables are blocked.')
  if (ast.limit?.value?.some(item => Number(item.value) > analyticsLimits.maxRows)) throw new Error('The result limit exceeds 500 rows.')
  const allowed = new Set((allowedTables || []).map(value => String(value).toLowerCase()))
  for (const item of tables) {
    if (item.database && item.database.toLowerCase() !== String(sourceDatabase).toLowerCase()) {
      throw new Error(`Query attempted to access an unapproved database: ${item.database}`)
    }
    if (!allowed.has(String(item.table).toLowerCase())) throw new Error(`Query attempted to access an unapproved table: ${item.table}`)
  }
  if (!tables.length) throw new Error('The analytics query does not reference an approved HRMS table.')
  if (!allowSensitive) {
    const selectedSensitive = columns.some(column => {
      const name = String(column).split('::').pop()
      return name !== '*' && sensitive.test(name)
    })
    if (selectedSensitive) throw new Error('The query requests personally identifiable data that is unavailable in CEO analytics mode.')
  }
  if (!/\blimit\s+\d+/i.test(sql)) sql = `${sql}\nLIMIT ${analyticsLimits.maxRows}`
  return { sql, ast, tables, columns }
}

function and(left, right) {
  return left ? { type: 'binary_expr', operator: 'AND', left, right } : right
}

export function applyClientScope(sql, clientId, catalog = []) {
  if (!clientId) return sql
  const ast = parser.astify(sql, { database: 'mysql' })
  if (Array.isArray(ast) || ast?.type !== 'select' || !Array.isArray(ast.from)) throw new Error('Client scope could not be applied safely.')
  const byName = new Map(catalog.map(table => [String(table.name).toLowerCase(), table]))
  function restrict(select) {
  let scoped = 0
  let requiredScope = false
  for (const source of select.from || []) {
    if (source.join && /RIGHT|FULL/i.test(source.join)) throw new Error('Use base tables and LEFT/INNER joins for client-scoped analytics.')
    const table = byName.get(String(source.table || '').toLowerCase())
    if (!table) continue
    const clientColumn = table.name.toLowerCase() === 'clients'
      ? table.columns.find(column => /^id$/i.test(column.name))
      : table.columns.find(column => /^(clientid|client_id)$/i.test(column.name))
    if (!clientColumn) continue
    const alias = source.as || source.table
    const condition = {
      type: 'binary_expr',
      operator: '=',
      left: { type: 'column_ref', table: alias, column: clientColumn.name, collate: null },
      right: { type: 'number', value: Number(clientId) },
    }
    // Preserve missing optional records (e.g. employees with no assigned campus).
    if (/LEFT/i.test(source.join || '')) source.on = and(source.on, condition)
    else { select.where = and(select.where, condition); requiredScope = true }
    scoped++
  }
  if (!scoped && (select.from || []).some(source => source.table)) throw new Error('The generated query cannot be safely restricted to the requested client. Join the client-owned parent table.')
  // A LEFT parent's ON predicate does not restrict an unowned base table: its
  // other-client records survive as unmatched rows and can still be counted.
  if (select.from?.[0]?.table && scoped && !requiredScope) throw new Error('The generated query cannot be safely restricted to the requested client using only LEFT joins. Use INNER JOIN for the required client-owned parent.')
  }
  function walk(node) {
    if (!node || typeof node !== 'object') return
    if (Array.isArray(node)) { node.forEach(walk); return }
    if (node.type === 'select') restrict(node)
    Object.values(node).forEach(walk)
  }
  walk(ast)
  return parser.sqlify(ast, { database: 'mysql' })
}
