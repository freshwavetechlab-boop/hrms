// Optional host metadata, never a field from the generated model payload. General
// SQL/RBAC validation still runs independently; unknown/complex questions have no
// contract and retain their existing path. No SQL or result is synthesized here.
export function validateLocalSimpleCountPlan(ast, contract, parameters = []) {
  if (contract == null) return
  const aggregate = ['sum_values', 'average_values'].includes(contract.measure)
  const fail = message => { throw new Error(`Local ${aggregate ? 'aggregate' : 'count'} plan does not match the requested metric: ${message}`) }
  const lower = value => String(value || '').toLowerCase()
  const name = value => typeof value === 'string' && /^[a-z_][a-z0-9_]*$/i.test(value)
  if (contract.version !== 'simple-count-v1' || (contract.measure !== 'count_records' && !aggregate)
    || contract.noOtherFiltersRequested !== true || !name(contract.recordTable)
    || !name(contract.grouping?.table) || !name(contract.grouping?.column)
    || !Array.isArray(contract.allowedTables) || contract.allowedTables.length > 3
    || !contract.allowedTables.every(name)
    || !Array.isArray(contract.requestedFilters) || !Array.isArray(contract.recordDefinitionFilters)
    || !Array.isArray(contract.requiredRelationships) || contract.requiredRelationships.length > 2) fail('unsupported host contract.')
  if (aggregate && (!name(contract.measureColumn?.table) || !name(contract.measureColumn?.column)
    || lower(contract.measureColumn.table) !== lower(contract.recordTable)
    || (contract.roundDigits != null && (contract.measure !== 'average_values' || contract.roundDigits !== 2)))) fail('unsupported host aggregate contract.')
  if (!Array.isArray(parameters)) fail('query parameters must be an array.')
  if (contract.scopeTables != null && (!Array.isArray(contract.scopeTables) || contract.scopeTables.length > 2
    || !contract.scopeTables.every(name))) fail('unsupported host scope.')
  const filters = [...contract.requestedFilters, ...contract.recordDefinitionFilters]
  if (filters.length > 3 || filters.some(filter => !name(filter.table) || !name(filter.column)
    || filter.operator !== '=' || !['number', 'string'].includes(typeof filter.value))) fail('unsupported host filters.')
  if (ast?.type !== 'select' || ast.with || ast._next || ast.having || ast.window || ast.distinct) fail('use one plain grouped SELECT without HAVING, DISTINCT, windows or nested queries.')
  let selectCount = 0
  function inspect(node) {
    if (!node || typeof node !== 'object') return
    if (node.type === 'select') selectCount++
    Object.values(node).forEach(inspect)
  }
  inspect(ast)
  if (selectCount !== 1) fail(`subqueries are not part of this simple ${aggregate ? 'aggregate' : 'count'}.`)
  const sources = ast.from || []
  const allowed = new Set(contract.allowedTables.map(lower))
  const record = lower(contract.recordTable)
  if (!sources.length || sources.length > allowed.size || lower(sources[0].table) !== record
    || sources.some(source => !allowed.has(lower(source.table)) || source.expr || source.using)) fail(`${aggregate ? 'aggregate' : 'count'} only the requested record table and required host relationships.`)
  if (new Set(sources.map(source => lower(source.table))).size !== sources.length) fail('duplicate table joins can change record counts.')
  const aliases = new Map(sources.map(source => [lower(source.as || source.table), lower(source.table)]))
  if (aliases.size !== sources.length) fail('table aliases must be unique.')
  const column = expr => {
    if (expr?.type !== 'column_ref' || !name(expr.column)) return null
    if (aggregate && expr.collate) return null
    const table = expr.table ? aliases.get(lower(expr.table)) : sources.length === 1 ? record : null
    return table ? { table, column: lower(expr.column) } : null
  }
  const same = (left, right) => left && right && lower(left.table) === lower(right.table) && lower(left.column) === lower(right.column)
  const grouping = { table: lower(contract.grouping.table), column: lower(contract.grouping.column) }
  const grouped = ast.groupby?.columns || []
  if (grouped.length !== 1 || !same(column(grouped[0]), grouping) || ast.groupby?.modifiers?.some(Boolean)) fail('GROUP BY must contain exactly the requested grouping column.')
  const isCount = expr => {
    if (expr?.type !== 'aggr_func' || lower(expr.name) !== 'count' || expr.over || expr.args?.orderby || expr.args?.separator) return false
    const argument = expr.args?.expr
    if (argument?.type === 'star') return !expr.args.distinct
    return same(column(argument), { table: record, column: 'id' })
      && (!expr.args.distinct || expr.args.distinct === 'DISTINCT')
  }
  const aggregateName = contract.measure === 'sum_values' ? 'sum' : 'avg'
  const isAggregate = expr => expr?.type === 'aggr_func' && lower(expr.name) === aggregateName
    && !expr.over && !expr.filter && !expr.within_group_orderby
    && !expr.args?.distinct && !expr.args?.orderby && !expr.args?.separator
    && same(column(expr.args?.expr), contract.measureColumn)
  const isMeasure = !aggregate ? isCount : expr => {
    if (contract.roundDigits == null) return isAggregate(expr)
    const functionName = typeof expr?.name === 'string' ? expr.name : expr?.name?.name?.map(part => part.value).join('.')
    const args = expr?.args?.value
    return expr?.type === 'function' && lower(functionName) === 'round' && !expr.over
      && expr.args?.type === 'expr_list' && args?.length === 2
      && isAggregate(args[0]) && args[1]?.type === 'number' && args[1].value === contract.roundDigits
  }
  const selected = ast.columns || []
  if (selected.length !== 2 || selected.filter(item => same(column(item.expr), grouping)).length !== 1
    || selected.filter(item => isMeasure(item.expr)).length !== 1) fail(aggregate
      ? 'select the requested grouping and exact SUM/AVG column, preserving explicitly requested ROUND precision and adding no other output.'
      : 'select the requested grouping and COUNT(*) or COUNT(record.Id), without changing the measure.')
  const outputs = selected.map(item => lower(item.as || item.expr.column || (aggregate ? aggregateName : 'count')))
  if (new Set(outputs).size !== outputs.length) fail(`group and ${aggregate ? 'aggregate' : 'count'} output names must differ.`)
  if (ast.orderby?.some(item => !isMeasure(item.expr) && !same(column(item.expr), grouping)
    && !(item.expr?.type === 'column_ref' && !item.expr.table && outputs.includes(lower(item.expr.column)))
    && !(item.expr?.type === 'number' && [1, 2].includes(item.expr.value)))) fail(`order only by the requested group or ${aggregate ? 'aggregate' : 'count'}.`)
  // A generated small LIMIT/offset could silently omit requested categories. The
  // existing host applies its ordinary 500-row safety cap when LIMIT is omitted.
  if (ast.limit && (ast.limit.value?.length !== 1 || ast.limit.value[0]?.type !== 'number'
    || ast.limit.value[0].value !== 500)) fail('do not omit groups with an early LIMIT or offset.')
  const relationships = contract.requiredRelationships.map(value => {
    if (!name(value?.leftTable) || !name(value.leftColumn) || !name(value.rightTable) || lower(value.rightColumn) !== 'id') fail('unsupported host relationship.')
    // Only the explicit application-by-job contract requires matching jobs.
    // Existing optional client-parent relationships retain their LEFT rules.
    if (value.requiredJoinType != null && !(value.requiredJoinType === 'INNER JOIN'
      && contract.measure === 'count_records' && record === 'recruitment_candidate_applications'
      && lower(value.leftTable) === record && lower(value.leftColumn) === 'positionid'
      && lower(value.rightTable) === 'recruitment_open_positions'
      && grouping.table === 'recruitment_open_positions' && grouping.column === 'positiontitle')) fail('unsupported host relationship join type.')
    return { from: { table: lower(value.leftTable), column: lower(value.leftColumn) },
      to: { table: lower(value.rightTable), column: 'id' }, requiredJoinType: value.requiredJoinType }
  })
  const needed = new Set([record, grouping.table, ...filters.map(filter => lower(filter.table)), ...(contract.scopeTables || []).map(lower)])
  // Client-owned parents can also be necessary solely for later host RBAC scope.
  for (let i = relationships.length - 1; i >= 0; i--) if (needed.has(relationships[i].to.table)) needed.add(relationships[i].from.table)
  const joined = new Set([record])
  for (const source of sources.slice(1)) {
    const table = lower(source.table)
    const relationship = relationships.find(item => item.to.table === table && joined.has(item.from.table))
    if (!needed.has(table) || !relationship || source.on?.type !== 'binary_expr' || source.on.operator !== '=') fail('an unrequested or fanout-producing join was added.')
    const left = column(source.on.left), right = column(source.on.right)
    if (!(same(left, relationship.from) && same(right, relationship.to))
      && !(same(right, relationship.from) && same(left, relationship.to))) fail('use the exact host-proven relationship.')
    // LEFT preserves records with a missing optional client. INNER is equivalent
    // only when a requested client filter already requires a matching parent.
    const requiresClient = filters.some(filter => lower(filter.table) === 'clients')
    if (relationship.requiredJoinType === 'INNER JOIN') {
      if (!['INNER JOIN', 'JOIN'].includes(source.join)) fail('use INNER JOIN for the explicitly requested jobs having applications.')
    } else if (table === 'recruitment_open_positions' && source.join !== 'LEFT JOIN') {
      fail('preserve records without an optional job using LEFT JOIN unless matching jobs were explicitly requested.')
    } else if (source.join !== 'LEFT JOIN' && !(requiresClient && ['INNER JOIN', 'JOIN'].includes(source.join))) fail('preserve records without an optional parent using LEFT JOIN.')
    joined.add(table)
  }
  if ([...needed].some(table => !joined.has(table))) fail('a relationship required for grouping, filtering or scope is missing.')
  const actualFilters = []
  let parameterIndex = 0
  const literal = expr => {
    if (expr?.type === 'origin' && expr.value === '?') {
      if (parameterIndex >= parameters.length) fail('a requested filter parameter is missing.')
      return String(parameters[parameterIndex++])
    }
    if (['number', 'single_quote_string', 'double_quote_string', 'string'].includes(expr?.type)) return String(expr.value)
    fail('only exact requested equality filters are allowed.')
  }
  function readFilters(expr) {
    if (!expr) return
    if (expr.type === 'binary_expr' && expr.operator === 'AND') { readFilters(expr.left); readFilters(expr.right); return }
    if (expr.type !== 'binary_expr' || expr.operator !== '=') fail('an unrequested filter was added.')
    const left = column(expr.left), right = column(expr.right)
    if ((!left && !right) || (left && right)) fail('only exact requested equality filters are allowed.')
    const field = left || right
    actualFilters.push({ ...field, value: literal(left ? expr.right : expr.left) })
  }
  readFilters(ast.where)
  if (!Array.isArray(parameters) || parameterIndex !== parameters.length) fail('query parameters do not match requested filters.')
  const key = filter => `${lower(filter.table)}.${lower(filter.column)}=${String(filter.value)}`
  if (JSON.stringify(actualFilters.map(key).sort()) !== JSON.stringify(filters.map(key).sort())) fail('WHERE must match exactly the requested filters and record definition; do not add implicit status, client or date filters.')
}
