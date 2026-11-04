import { isLiteralBlankPayslipCell, type PayslipColumn, type PayslipSheet } from './excelPayslipImport'

const locationKey = (value: string) => value.toLowerCase().replace(/[^a-z0-9]/g, '')
export const workLocationColumns = (columns: PayslipColumn[]) => columns.filter(column => column.kind === 'info'
  && [column.label, column.sourceHeader].some(value => /^(?:worklocation|location|placeofposting|posting|locationnametehsilsubtehsil)$/.test(locationKey(value))))

// A batch label or filename is not employee data. Only an operator's explicit
// value may fill literal blank locations on the reviewed employee rows.
export function fillMissingPayslipLocations(sheet: PayslipSheet, columns: PayslipColumn[], sourceRows: number[], location: string) {
  const value = location.trim(), mapped = workLocationColumns(columns)
  if (!value || value.length > 190 || /[\r\n]/.test(value)) throw new Error('Enter one work location of up to 190 characters.')
  if (mapped.length !== 1) throw new Error('Map exactly one Work Location column as Information before filling blank locations.')
  const index = mapped[0].columnIndex, employees = new Set(sourceRows)
  let changed = 0
  const rows = sheet.rows.map(row => {
    if (!employees.has(row.sourceRow) || !isLiteralBlankPayslipCell(row.cells[index])) return row
    changed++
    const cells = [...row.cells]
    cells[index] = { value, dataType: 'Text' }
    return { ...row, cells }
  })
  return { sheet: changed ? { ...sheet, rows } : sheet, changed }
}
