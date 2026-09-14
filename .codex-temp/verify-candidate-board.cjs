const { chromium } = require('C:/Users/EESL/AppData/Local/npm-cache/_npx/e41f203b7505f1fb/node_modules/playwright');
const crypto = require('crypto');
const fs = require('fs');
const { spawnSync } = require('child_process');
const mysqlExe = 'C:/Program Files/MySQL/MySQL Server 8.4/bin/mysql.exe';
const config = JSON.parse(fs.readFileSync('Payroll.API/appsettings.json', 'utf8'));
const connection = Object.fromEntries(config.ConnectionStrings.Default.split(';').filter(Boolean).map(part => { const index = part.indexOf('='); return [part.slice(0, index).trim().toLowerCase(), part.slice(index + 1).trim()]; }));
function sql(statement) {
  const result = spawnSync(mysqlExe, ['-h', connection.server, '-P', connection.port || '3306', '-u', connection['user id'] || connection.uid, '-D', connection.database, '--batch', '--skip-column-names'], { input: statement, encoding: 'utf8', env: { ...process.env, MYSQL_PWD: connection.password || connection.pwd || '' } });
  if (result.status !== 0) throw new Error(result.stderr || `mysql exited ${result.status}`);
}
async function main() {
  const token = crypto.randomBytes(32).toString('base64');
  const tokenHash = crypto.createHash('sha256').update(token).digest('hex');
  sql(`INSERT INTO authsessions (UserId,TokenHash,IpAddress,UserAgent,ExpiresAt) VALUES (3,'${tokenHash}','127.0.0.1','Frevo Playwright Board Verification',DATE_ADD(UTC_TIMESTAMP(),INTERVAL 2 HOUR));`);
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
    await context.addCookies([{ name: 'payroll_auth', value: token, domain: 'localhost', path: '/', httpOnly: true, sameSite: 'Lax' }]);
    const page = await context.newPage();
    const consoleErrors = [];
    let uiWorkspace = null;
    page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()); });
    page.on('response', async response => {
      if (response.url().includes('/api/recruitment/pipeline-workspace')) {
        try { uiWorkspace = await response.json(); } catch { /* response diagnostics only */ }
      }
    });
    const apiResponse = await page.request.get('http://localhost:5062/api/recruitment/pipeline-workspace?clientId=20');
    const workspace = await apiResponse.json();
    await page.goto('http://localhost:5173/recruitment/hiring-pipeline?clientId=20&flow=candidates', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(2500);
    await page.screenshot({ path: '.codex-temp/candidate-board.png', fullPage: true });
    console.log(JSON.stringify({ status: apiResponse.status(), lanes: workspace.lanes?.map(lane => ({ pipeline: lane.pipelineName, stage: lane.stageName, scope: lane.cardScope })) || [], uiLaneCount: uiWorkspace?.lanes?.length ?? null, uiScopes: uiWorkspace?.lanes?.map(lane => lane.cardScope) ?? [], consoleErrors, body: (await page.locator('body').innerText()).slice(0, 5000) }, null, 2));
  } finally {
    await browser.close();
    sql(`UPDATE authsessions SET RevokedAt=UTC_TIMESTAMP() WHERE TokenHash='${tokenHash}' AND RevokedAt IS NULL;`);
  }
}
main().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
