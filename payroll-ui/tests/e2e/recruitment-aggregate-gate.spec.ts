import { expect, test } from '@playwright/test'

const baseUrl = 'http://localhost:5173'
const apiUrl = 'http://localhost:5062'

test('vacancy stays at Order for Hiring while linked candidates remain unresolved', async ({ page }) => {
  await page.goto(`${baseUrl}/recruitment/hiring-pipeline?clientId=20&positionId=14&flow=hiring`)

  const signIn = page.getByRole('button', { name: 'Sign in' })
  await expect(signIn).toBeVisible({ timeout: 20_000 })
  await page.getByLabel('Email').fill('admin@paymint.local')
  await page.getByPlaceholder('Enter password').fill('Admin@12345')
  await signIn.click()
  await page.waitForURL('**/dashboard', { timeout: 20_000 })
  await page.goto(`${baseUrl}/recruitment/hiring-pipeline?clientId=20&positionId=14&flow=hiring`)
  const token = await page.evaluate(() => sessionStorage.getItem('payroll.auth.token'))
  expect(token).toBeTruthy()
  const apiOptions = { headers: { Authorization: `Bearer ${token}` } }

  const [applicationsResponse, workspaceResponse, interviewsResponse] = await Promise.all([
    page.request.get(`${apiUrl}/api/recruitment/applications?positionId=14`, apiOptions),
    page.request.get(`${apiUrl}/api/recruitment/pipeline-workspace?clientId=20&positionId=14`, apiOptions),
    page.request.get(`${apiUrl}/api/recruitment/interviews`, apiOptions),
  ])
  expect(applicationsResponse.ok()).toBeTruthy()
  expect(workspaceResponse.ok()).toBeTruthy()
  expect(interviewsResponse.ok()).toBeTruthy()

  const applications = await applicationsResponse.json()
  const workspace = await workspaceResponse.json()
  const interviews = await interviewsResponse.json()
  const positionApplications = applications.filter((row: { positionId: number }) => row.positionId === 14)
  const unresolved = positionApplications.filter((row: { currentStage: string }) => row.currentStage === 'ATS Screening & JD Match')
  const applicationIds = new Set(positionApplications.map((row: { id: number }) => row.id))
  const scheduled = interviews.filter((row: { applicationId: number; status: string }) => applicationIds.has(row.applicationId) && ['Scheduled', 'Rescheduled', 'Completed'].includes(row.status))
  const demandCard = workspace.lanes.flatMap((lane: { demandCards: unknown[] }) => lane.demandCards)
    .find((row: { positionId: number }) => row.positionId === 14)

  expect(positionApplications).toHaveLength(6)
  expect(unresolved).toHaveLength(4)
  expect(scheduled).toHaveLength(2)
  expect(demandCard.currentStageName).toBe('Order for Hiring (Work Order issued)')

  const card = page.getByTestId('pipeline-demand-15')
  await expect(card).toBeVisible({ timeout: 30_000 })
  await expect(card).toContainText('Platform Engineer')
  await expect(card).toContainText('Order for Hiring (Work Order issued)')
  await expect(card).toContainText('6 candidates')
  await expect(page.getByText('Loading...', { exact: true })).toBeHidden({ timeout: 10_000 })

  await page.screenshot({
    path: '../artifacts/playwright/platform-engineer-aggregate-gate.png',
    fullPage: true,
  })
})
