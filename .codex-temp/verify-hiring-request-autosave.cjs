const { chromium } = require('C:/Users/EESL/AppData/Local/npm-cache/_npx/e41f203b7505f1fb/node_modules/playwright');
const crypto = require('crypto');
const fs = require('fs');
const { spawnSync } = require('child_process');

const mysqlExe = 'C:/Program Files/MySQL/MySQL Server 8.4/bin/mysql.exe';
const config = JSON.parse(fs.readFileSync('Payroll.API/appsettings.json', 'utf8'));
const connection = Object.fromEntries(config.ConnectionStrings.Default.split(';').filter(Boolean).map(part => {
  const index = part.indexOf('=');
  return [part.slice(0, index).trim().toLowerCase(), part.slice(index + 1).trim()];
}));
function sql(statement) {
  const result = spawnSync(mysqlExe, ['-h', connection.server, '-P', connection.port || '3306', '-u', connection['user id'] || connection.uid, '-D', connection.database, '--batch', '--skip-column-names'], {
    input: statement, encoding: 'utf8', env: { ...process.env, MYSQL_PWD: connection.password || connection.pwd || '' },
  });
  if (result.status !== 0) throw new Error(result.stderr || `mysql exited ${result.status}`);
}

async function main() {
  const token = crypto.randomBytes(32).toString('base64');
  const tokenHash = crypto.createHash('sha256').update(token).digest('hex');
  sql(`INSERT INTO authsessions (UserId,TokenHash,IpAddress,UserAgent,ExpiresAt) VALUES (3,'${tokenHash}','127.0.0.1','Autosave verification',DATE_ADD(UTC_TIMESTAMP(),INTERVAL 20 MINUTE));`);
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 960 } });
    await context.addCookies([{ name: 'payroll_auth', value: token, domain: 'localhost', path: '/', httpOnly: true, sameSite: 'Lax' }]);
    const page = await context.newPage();
    let serverDraftWrites = 0;
    await page.route('**/api/recruitment/requisitions', async route => {
      const request = route.request();
      if (request.method() !== 'POST') return route.continue();
      serverDraftWrites += 1;
      const body = request.postDataJSON();
      const now = new Date().toISOString();
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
        ...body, id: body.id || 987654321, rfrNumber: 'AUTOSAVE-VERIFY', requestedByName: 'Bashisth Gupt',
        clientName: 'Unique Identification Authority of India', branchName: '', replacementEmployeeName: '',
        status: 'Draft', createdAt: now, updatedAt: now,
      }) });
    });
    await page.goto('http://localhost:5173/recruitment/requisitions?clientId=20&new=1', { waitUntil: 'domcontentloaded' });
    const role = page.locator('input[placeholder="For example, Senior .NET Engineer"]');
    await role.waitFor().catch(async error => {
      throw new Error(`${error.message}\nURL: ${page.url()}\nBODY: ${(await page.locator('body').innerText()).slice(0, 1800)}`);
    });
    await role.fill('Refresh-safe autosave verification');
    await page.waitForTimeout(1500);
    const before = await page.getByTestId('hiring-request-autosave-status').innerText();
    await page.reload({ waitUntil: 'domcontentloaded' });
    const restoredRole = page.locator('input[placeholder="For example, Senior .NET Engineer"]');
    await restoredRole.waitFor();
    await page.waitForFunction(() => document.querySelector('input[placeholder="For example, Senior .NET Engineer"]')?.value === 'Refresh-safe autosave verification');
    const restored = await restoredRole.inputValue();
    const after = await page.getByTestId('hiring-request-autosave-status').innerText();
    const writesBeforeValid = serverDraftWrites;
    await page.locator('.ant-form-item').filter({ hasText: /^Department/ }).locator('input').fill('Information Technology');
    await page.waitForFunction(() => document.querySelector('[data-testid="hiring-request-autosave-status"]')?.textContent?.includes('Draft saved automatically'));
    const serverStatus = await page.getByTestId('hiring-request-autosave-status').innerText();
    const saveDraftButtons = await page.getByRole('button', { name: /^Save draft$|^Update draft$/ }).count();
    const submitButtons = await page.getByTestId('save-submit-requisition').count();
    console.log(JSON.stringify({ before, after, restored, writesBeforeValid, serverDraftWrites, serverStatus, saveDraftButtons, submitButtons }, null, 2));
    if (restored !== 'Refresh-safe autosave verification' || writesBeforeValid !== 0 || serverDraftWrites !== 1 || !serverStatus.includes('Draft saved automatically') || saveDraftButtons !== 0 || submitButtons !== 1) process.exitCode = 1;
  } finally {
    await browser.close();
    sql(`UPDATE authsessions SET RevokedAt=UTC_TIMESTAMP() WHERE TokenHash='${tokenHash}' AND RevokedAt IS NULL;`);
  }
}
main().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
