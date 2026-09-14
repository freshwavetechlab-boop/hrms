const { chromium } = require('C:/Users/EESL/AppData/Local/npm-cache/_npx/e41f203b7505f1fb/node_modules/playwright');
const crypto = require('crypto');
const fs = require('fs');
const { spawnSync } = require('child_process');

const root = 'D:/NewHrms/hrms';
const mysqlExe = 'C:/Program Files/MySQL/MySQL Server 8.4/bin/mysql.exe';
const jdPath = 'C:/Users/EESL/Downloads/reopeningofhiringfor7positionsoftechcentrebengalu/JD_Platform Engineer Band D.pdf';
const resumeFolder = 'C:/Users/EESL/Downloads/download (27)';
const statePath = `${root}/.codex-temp/recruitment-e2e-state.json`;
const config = JSON.parse(fs.readFileSync(`${root}/Payroll.API/appsettings.json`, 'utf8'));
const connection = Object.fromEntries(config.ConnectionStrings.Default.split(';').filter(Boolean).map(part => {
  const index = part.indexOf('=');
  return [part.slice(0, index).trim().toLowerCase(), part.slice(index + 1).trim()];
}));

function sql(statement) {
  let lastError = '';
  for (let attempt = 1; attempt <= 5; attempt += 1) {
    const result = spawnSync(mysqlExe, ['--connect-timeout=8', '-h', connection.server, '-P', connection.port || '3306', '-u', connection['user id'] || connection.uid, '-D', connection.database, '--batch', '--skip-column-names'], {
      input: statement, encoding: 'utf8', env: { ...process.env, MYSQL_PWD: connection.password || connection.pwd || '' }
    });
    if (result.status === 0) return result.stdout.trim();
    lastError = result.stderr || `mysql exited ${result.status}`;
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 1500);
  }
  throw new Error(lastError);
}

function writeState(value) { fs.writeFileSync(statePath, JSON.stringify(value, null, 2)); }
function readState() { return fs.existsSync(statePath) ? JSON.parse(fs.readFileSync(statePath, 'utf8')) : {}; }
function queueSnapshot() {
  const [maxId = '0', pending = '0'] = sql("SELECT COALESCE(MAX(Id),0),COALESCE(SUM(Status IN ('Pending','Retry','Processing')),0) FROM notification_queue;").split('\t');
  return { maxId: Number(maxId), pending: Number(pending) };
}

async function selectAnt(page, locator, optionText) {
  await locator.click();
  const dropdown = page.locator('.ant-select-dropdown:visible').last();
  await dropdown.waitFor({ state: 'visible' });
  const search = locator.locator('input').first();
  if (await search.count() && (await search.getAttribute('readonly')) === null) await search.fill(optionText);
  const option = dropdown.locator('.ant-select-item-option').filter({ hasText: optionText }).last();
  await option.scrollIntoViewIfNeeded();
  await option.click();
}

async function authContext(browser) {
  const token = crypto.randomBytes(32).toString('base64');
  const tokenHash = crypto.createHash('sha256').update(token).digest('hex');
  sql(`INSERT INTO authsessions (UserId,TokenHash,IpAddress,UserAgent,ExpiresAt) VALUES (3,'${tokenHash}','127.0.0.1','Frevo Playwright E2E',DATE_ADD(UTC_TIMESTAMP(),INTERVAL 4 HOUR));`);
  const context = await browser.newContext({ viewport: { width: 1600, height: 1000 }, acceptDownloads: true });
  await context.addCookies([{ name: 'payroll_auth', value: token, domain: 'localhost', path: '/', httpOnly: true, sameSite: 'Lax' }]);
  return { context, revoke: () => sql(`UPDATE authsessions SET RevokedAt=UTC_TIMESTAMP() WHERE TokenHash='${tokenHash}' AND RevokedAt IS NULL;`) };
}

function monitor(page) {
  const errors = [];
  page.on('pageerror', error => errors.push(`page: ${error.message}`));
  page.on('console', message => {
    if (message.type() === 'error' && !message.text().includes('element.ref was removed in React 19')) errors.push(`console: ${message.text()}`);
  });
  page.on('response', response => {
    if (response.status() >= 400) errors.push(`${response.status()} ${response.request().method()} ${response.url()}`);
  });
  return errors;
}

async function waitJson(page, path, action, timeout = 120000) {
  const responsePromise = page.waitForResponse(response => response.url().includes(path) && ['POST', 'PUT', 'DELETE'].includes(response.request().method()), { timeout });
  try {
    await action();
  } catch (error) {
    responsePromise.catch(() => {});
    throw error;
  }
  const response = await responsePromise;
  const body = await response.json().catch(() => null);
  if (!response.ok()) throw new Error(`${response.status()} ${path}: ${JSON.stringify(body)}`);
  return body;
}

async function phaseIntake(page, state, resumeExisting = false) {
  if (!resumeExisting) {
    const runCode = `PW-E2E-${Date.now()}`;
    state.runCode = runCode;
    state.queueBefore = queueSnapshot();
    writeState(state);

    await page.goto('http://localhost:5173/recruitment/work-orders-and-sla', { waitUntil: 'networkidle' });
    await page.getByTestId('work-order-add').click();
    await selectAnt(page, page.getByTestId('work-order-client'), 'Unique Identification Authority of India');
    await page.getByTestId('work-order-number').fill(runCode);
    await page.getByTestId('work-order-received-at').fill(new Date().toISOString().slice(0, 16));
    await page.getByTestId('work-order-received-from').fill('Local Playwright regression - no email');
    await selectAnt(page, page.getByTestId('work-order-status'), 'Active');
    await page.getByTestId('work-order-subject').fill('Platform Engineer Band D end-to-end regression');
    await page.getByTestId('work-order-remarks').fill('Automated local QA; outbound delivery suppressed.');
    const workOrder = await waitJson(page, '/api/recruitment/work-orders', () => page.getByTestId('work-order-save').click());
    state.workOrder = workOrder;
    writeState(state);
  } else {
    await page.goto(`http://localhost:5173/recruitment/work-orders-and-sla?workOrderId=${state.workOrder.id}`, { waitUntil: 'networkidle' });
    await page.getByTestId('work-order-add-hiring-request').waitFor({ state: 'visible', timeout: 60000 });
  }

  await page.getByTestId('work-order-add-hiring-request').evaluate(element => element.click());
  await page.getByTestId('hiring-request-source-file').setInputFiles(jdPath);
  await page.getByTestId('hiring-request-source-result').waitFor({ state: 'visible', timeout: 120000 });
  state.jdParseSummary = (await page.getByTestId('hiring-request-source-result').innerText()).trim();

  const required = ['Role / position', 'Department', 'Openings'];
  state.prefill = {};
  for (const label of required) {
    const field = page.getByLabel(label, { exact: true });
    state.prefill[label] = await field.inputValue().catch(() => '');
  }
  const positionCategory = page.getByTestId('rfr-position-category');
  if (!(await positionCategory.locator('.ant-select-selection-item').count())) {
    await selectAnt(page, positionCategory, 'Technical');
  }

  const requisition = await waitJson(page, '/api/recruitment/requisitions', () =>
    page.locator('.rfr-dialog-actions button').filter({ hasText: /^Save draft$/ }).click());
  state.requisition = requisition;
  writeState(state);

  await page.getByText('Job description & ATS screening', { exact: true }).click();
  await page.getByTestId('jd-step-screening').waitFor({ state: 'visible', timeout: 60000 });
  await page.getByTestId('jd-step-screening').click();
  await page.getByText('Skills & ATS scoring', { exact: true }).first().waitFor({ state: 'visible' });
  state.jdWorkspaceText = (await page.locator('.rfr-jd-workspace').innerText()).slice(0, 8000);
  const savedAgain = await waitJson(page, '/api/recruitment', () => page.getByRole('button', { name: 'Update draft', exact: true }).click());
  state.requisitionAfterJdSave = savedAgain;
  writeState(state);

  const submitted = await waitJson(page, `/api/recruitment/requisitions/${requisition.id}/submit`, () => page.getByTestId('save-submit-requisition').click());
  state.requisitionSubmitted = submitted;
  writeState(state);
  await page.screenshot({ path: `${root}/.codex-temp/e2e-after-requisition.png`, fullPage: true });
  return state;
}

async function main() {
  const phase = process.argv[2] || 'intake';
  const browser = await chromium.launch({ headless: true });
  const { context, revoke } = await authContext(browser);
  const page = await context.newPage();
  const errors = monitor(page);
  let state = readState();
  try {
    if (phase === 'intake') {
      state = {};
      await phaseIntake(page, state);
    } else if (phase === 'resume-intake') {
      const [id, runCode, clientId] = sql("SELECT Id,WorkOrderNumber,ClientId FROM recruitment_work_orders WHERE WorkOrderNumber LIKE 'PW-E2E-%' AND NOT EXISTS (SELECT 1 FROM recruitment_work_order_lines l WHERE l.WorkOrderId=recruitment_work_orders.Id AND l.RequisitionId IS NOT NULL) ORDER BY Id DESC LIMIT 1;").split('\t');
      if (!id) throw new Error('No resumable Playwright work order found.');
      state = { runCode, queueBefore: queueSnapshot(), workOrder: { id: Number(id), clientId: Number(clientId), workOrderNumber: runCode } };
      await phaseIntake(page, state, true);
    }
    state.browserErrors = errors;
    state.queueAfter = queueSnapshot();
    writeState(state);
    console.log(JSON.stringify({ phase, runCode: state.runCode, workOrderId: state.workOrder?.id, requisitionId: state.requisition?.id, status: state.requisitionSubmitted?.status, jdParseSummary: state.jdParseSummary, prefill: state.prefill, queueBefore: state.queueBefore, queueAfter: state.queueAfter, errors }, null, 2));
  } catch (error) {
    state.failure = error.stack || String(error);
    state.browserErrors = errors;
    state.queueAfter = queueSnapshot();
    writeState(state);
    await page.screenshot({ path: `${root}/.codex-temp/e2e-failure.png`, fullPage: true }).catch(() => {});
    throw error;
  } finally {
    await context.close();
    await browser.close();
    try { revoke(); } catch (error) { console.error(`Auth-session cleanup warning: ${error.message}`); }
  }
}

main().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
