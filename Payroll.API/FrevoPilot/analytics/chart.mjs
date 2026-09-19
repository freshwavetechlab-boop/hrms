function numericValue(value) {
  if (typeof value === 'number' && Number.isFinite(value)) return value
  if (typeof value === 'string' && value.trim() !== '' && Number.isFinite(Number(value))) return Number(value)
  return null
}

function displayLabel(value, index) {
  if (value == null || value === '') return `Row ${index + 1}`
  return String(value)
}

const colors = [
  '#6548e8', '#1b86d9', '#14a38b', '#f29e38', '#e64c72', '#6e78f7',
  '#2fb7c4', '#99bf43', '#c96bd6', '#ef6c52', '#476fd1', '#17a673',
]

export function buildChart(rows, requested = {}) {
  const safeRows = Array.isArray(rows) ? rows : []
  const columns = [...new Set(safeRows.flatMap(row => Object.keys(row || {})))]
  const numericColumns = columns.filter(column => safeRows.some(row => numericValue(row?.[column]) !== null))
  const textColumns = columns.filter(column => !numericColumns.includes(column))
  const labelField = columns.includes(requested.labelField) ? requested.labelField : textColumns[0] || columns[0] || ''
  const selectedValues = (requested.valueFields || []).filter(field => numericColumns.includes(field))
  // Only the runtime's validated local host contract supplies these fields.
  // An all-null measure must remain missing instead of promoting a numeric group.
  const valueFields = Array.isArray(requested.validatedValueFields)
    ? requested.validatedValueFields.filter(field => columns.includes(field) && field !== labelField)
    : selectedValues.length ? selectedValues : numericColumns.slice(0, 4)
  let type = requested.chartType || 'auto'
  if (type === 'auto') {
    if (safeRows.length === 1 && valueFields.length) type = 'kpi'
    else if (!valueFields.length) type = 'table'
    else if (/date|month|period|year|time/i.test(labelField)) type = 'line'
    else if (safeRows.length <= 8 && valueFields.length === 1) type = 'doughnut'
    else type = 'bar'
  }
  if (type === 'kpi' && safeRows.length !== 1) type = 'bar'
  if (type === 'doughnut' && valueFields.length > 1) valueFields.splice(1)
  const labels = safeRows.map((row, index) => displayLabel(row?.[labelField], index))
  const datasets = valueFields.map((field, index) => ({
    label: field.replace(/_/g, ' '),
    data: safeRows.map(row => numericValue(row?.[field])),
    borderColor: colors[index % colors.length],
    backgroundColor: type === 'doughnut'
      ? labels.map((_, colorIndex) => colors[colorIndex % colors.length])
      : `${colors[index % colors.length]}cc`,
    borderWidth: 2,
    tension: 0.28,
  }))
  const primary = {
    type,
    labelField,
    valueFields,
    labels,
    datasets,
    columns,
    rowCount: safeRows.length,
    generatedAt: new Date().toISOString(),
  }
  const presentationMode = requested.presentationMode === 'dashboard' ? 'dashboard' : 'single'
  if (presentationMode === 'single') return { ...primary, presentationMode, widgets: [primary] }

  const widget = (widgetType, fields, title, options = {}) => ({
    ...primary,
    type: widgetType,
    valueFields: fields,
    datasets: fields.map((field, index) => ({
      label: field.replace(/_/g, ' '),
      data: safeRows.map(row => numericValue(row?.[field])),
      borderColor: colors[index % colors.length],
      backgroundColor: widgetType === 'doughnut'
        ? labels.map((_, colorIndex) => colors[colorIndex % colors.length])
        : `${colors[index % colors.length]}cc`,
      borderWidth: 2,
      tension: 0.28,
    })),
    title,
    options,
  })
  const widgets = [{ ...primary, title: requested.title || 'Primary decision view' }]
  if (valueFields.length && type !== 'bar' && type !== 'kpi') {
    widgets.push(widget('bar', valueFields.slice(0, 2), 'Category comparison'))
  }
  // Categorical data is not a time trend; averages are not shares of a whole.
  // Keep the primary chart and backing table rather than manufacturing more views.
  return { ...primary, presentationMode, widgets: widgets.slice(0, 2) }
}
