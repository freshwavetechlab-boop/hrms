// Run in the initialized CUA REPL with the existing, signed-in localhost tab:
// await verifyConsolidatedPayslipActions(payslipTab)
// For a batch with no verified location splits, pass { requiresReconciliation: false }.
// Only view controls and PDF option drawers are exercised. No save/email occurs.
async function verifyConsolidatedPayslipActions(tab, { requiresReconciliation = true } = {}) {
  const check = (value, message) => { if (!value) throw new Error(message) }
  const workspace = tab.playwright.getByRole('region', { name: 'Excel payslips workspace', exact: true })
  check(await workspace.getByLabel('Saved source employee summary', { exact: true }).count() === 0, 'Source inspector must not replace the default payslip list, including legacy Employees preferences.')
  check(await workspace.getByRole('columnheader', { name: /^(In Hand Salary|Net pay as stated)$/ }).count() > 0, 'The normal salary table must be rendered.')
  const rows = workspace.locator('.ant-table-tbody tr[data-row-key]')
  check(await rows.count() > 0, 'A representative consolidated batch is required.')
  const indexes = await workspace.getByRole('columnheader').evaluateAll(headers => headers.flatMap((header, index) => ['Source row', 'Employee', 'Employee code', 'Earnings', 'Deductions', 'In Hand Salary', 'Net pay as stated'].includes(header.textContent.trim()) ? [index] : []))
  const stableRows = () => rows.evaluateAll((items, columns) => items.map(row => ({ id: row.getAttribute('data-row-key'), values: columns.map(index => row.querySelectorAll('td')[index]?.textContent) })), indexes)
  const before = await stableRows()
  check(await workspace.getByRole('button', { name: /^Source parts for / }).count() === 0, 'Normal rows must not force a Source parts step.')
  await tab.playwright.getByRole('button', { name: 'More payslip actions', exact: true }).click()
  await tab.playwright.domSnapshot()
  const reconciliationAvailable = await tab.playwright.getByRole('menuitem', { name: 'Source reconciliation', exact: true }).count() === 1
  check(!requiresReconciliation || reconciliationAvailable, 'Reconciliation must remain available as an explicit secondary action.')
  if (reconciliationAvailable) {
    await tab.playwright.getByRole('menuitem', { name: 'Source reconciliation', exact: true }).click()
    await tab.playwright.domSnapshot()
    check(await workspace.getByLabel('Saved source employee summary', { exact: true }).count() === 1, 'Explicit source inspection must still work.')
    await workspace.getByRole('button', { name: 'Back to payslips', exact: true }).click()
    await tab.playwright.domSnapshot()
  } else {
    await tab.playwright.getByRole('button', { name: 'More payslip actions', exact: true }).click()
    await tab.playwright.domSnapshot()
  }
  check(await workspace.getByLabel('Saved source employee summary', { exact: true }).count() === 0, 'Returning must restore the normal list.')
  const after = await stableRows()
  check(JSON.stringify(before) === JSON.stringify(after), 'View switching must preserve source row identities and displayed values.')
  const first = rows.first()
  const compact = await first.getByRole('button', { name: /^Actions for / }).count() > 0
  for (const [action, expected] of [['Preview', 'Generate preview'], ['Download PDF', 'Generate & download PDF']]) {
    if (compact) {
      await first.getByRole('button', { name: /^Actions for / }).click()
      await tab.playwright.getByRole('menuitem', { name: action, exact: true }).click()
    } else await first.getByRole('button', { name: action, exact: true }).click()
    await tab.playwright.domSnapshot()
    const drawer = tab.playwright.getByRole('dialog').filter({ hasText: 'Payslip PDF' })
    check(await drawer.getByText('1 payslips', { exact: true }).count() === 1, 'Individual action must select exactly one original slip.')
    check(await drawer.getByRole('button', { name: expected, exact: true }).count() === 1, 'Normal PDF options must be used.')
    await drawer.getByRole('button', { name: 'Close', exact: true }).last().click()
    await tab.playwright.domSnapshot()
  }
  return { normalListDefault: true, reconciliationAvailable, rowIdentityAndAmountsPreserved: true, individualPreview: true, individualDownload: true, saves: 0, emails: 0 }
}
