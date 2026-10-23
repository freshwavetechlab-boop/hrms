import { useEffect, useMemo, useState } from 'react'
import { previewExcelPayslipCorrections } from '../services/excelPayslipService'
import { buildExcelPayslipRows, buildPayslipCalculationSource, parsePayslipAmount, type PayslipColumn, type PayslipSheet } from '../utils/excelPayslipImport'

export function usePayslipCorrectionPreview(sheet: PayslipSheet, columns: PayslipColumn[], edits: Record<number, Record<number, string | null>>, enabled: boolean) {
  const key = JSON.stringify(edits), clientId = sheet.template?.clientId || 0
  const [result, setResult] = useState<{ key: string; sheet?: PayslipSheet; error: string }>()
  const entered = Object.values(edits).some(values => Object.keys(values).length > 0)
  useEffect(() => {
    if (!enabled || !entered) return
    let active = true
    const controller = new AbortController()
    const timer = setTimeout(() => void (async () => {
      try {
        if (!sheet.template || !clientId) throw new Error('Choose a client and salary month before recalculating.')
        const edits = JSON.parse(key) as Record<number, Record<number, string | null>>
        const inputs = Object.entries(edits).flatMap(([row, values]) => Object.entries(values).map(([column, value]) => {
          const amount = value == null ? null : parsePayslipAmount(value)
          if (amount == null) throw new Error('Enter valid correction amounts before recalculating.')
          return { sourceRow: Number(row), columnIndex: Number(column), amount }
        }))
        const parsed = buildExcelPayslipRows(sheet, sheet.template.headerRow, columns, sheet.template.excludedSourceRows)
        if (parsed.issues.length) throw new Error(parsed.issues[0].message)
        const response = await previewExcelPayslipCorrections(clientId, { clientId, month: sheet.template.month, sourceFileName: 'Correction preview.xlsx', sheetName: sheet.name,
          headerRow: sheet.template.headerRow, rows: parsed.rows, calculationSource: buildPayslipCalculationSource(sheet, sheet.template.headerRow, columns, parsed.excludedRows) }, inputs, controller.signal)
        if (!active) return
        if (!response.ok || !response.data) throw new Error(response.error || 'Unable to recalculate saved formulas. Retry or upload the recalculated workbook.')
        setResult({ key, sheet: { ...sheet, rows: response.data.rows }, error: '' })
      } catch (error) { if (active) setResult({ key, error: error instanceof Error ? error.message : 'Unable to recalculate saved formulas.' }) }
    })(), 350)
    return () => { active = false; controller.abort(); clearTimeout(timer) }
  }, [sheet, columns, clientId, key, enabled, entered])
  const rows = useMemo(() => result?.key === key && result.sheet && sheet.template ? buildExcelPayslipRows(result.sheet, sheet.template.headerRow, columns, sheet.template.excludedSourceRows).rows : [], [result, key, sheet.template, columns])
  return { sheet: enabled && result?.key === key ? result.sheet : undefined, rows, error: enabled && result?.key === key ? result.error : '', loading: enabled && entered && result?.key !== key }
}
