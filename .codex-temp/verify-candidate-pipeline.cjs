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
    input: statement,
    encoding: 'utf8',
    env: { ...process.env, MYSQL_PWD: connection.password || connection.pwd || '' },
  });
  if (result.status !== 0) throw new Error(result.stderr || `mysql exited ${result.status}`);
  return result.stdout.trim();
}

async function fieldSummary(drawer) {
  return drawer.locator('.ant-form-item').evaluateAll(items => items.map(item => ({
    label: item.querySelector('.ant-form-item-label')?.textContent?.trim() || '',
    value: item.querySelector('.ant-select-selection-item')?.getAttribute('title')
      || item.querySelector('.ant-select-selection-item')?.textContent?.trim()
      || item.querySelector('input')?.value
      || '',
    checked: Array.from(item.querySelectorAll('input[type="checkbox"]')).map(input => input.checked),
  })).filter(row => row.label || row.value || row.checked.length));
}

async function main() {
  const token = crypto.randomBytes(32).toString('base64');
  const tokenHash = crypto.createHash('sha256').update(token).digest('hex');
  sql(`INSERT INTO authsessions (UserId,TokenHash,IpAddress,UserAgent,ExpiresAt) VALUES (3,'${tokenHash}','127.0.0.1','Frevo Playwright Verification',DATE_ADD(UTC_TIMESTAMP(),INTERVAL 2 HOUR));`);
  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext({ viewport: { width: 1600, height: 1000 } });
    await context.addCookies([{ name: 'payroll_auth', value: token, domain: 'localhost', path: '/', httpOnly: true, sameSite: 'Lax' }]);
    const page = await context.newPage();
    const failures = [];
    page.on('response', response => { if (response.status() >= 400) failures.push(`${response.status()} ${response.request().method()} ${response.url()}`); });
    await page.goto('http://localhost:5173/recruitment/hiring-pipeline?clientId=20&flow=hiring', { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(2500);
    const managePipeline = page.getByRole('button', { name: /Manage pipeline/i });
    if (await managePipeline.count()) {
      await managePipeline.click();
      await page.waitForTimeout(1200);
    }
    const pipelineTab = page.getByRole('tab', { name: /Pipeline design/i });
    if (!await pipelineTab.count()) {
      await page.screenshot({ path: '.codex-temp/candidate-pipeline-route-failure.png', fullPage: true });
      throw new Error(`Pipeline design tab missing at ${page.url()} :: ${(await page.locator('body').innerText()).slice(0, 1200)}`);
    }
    await pipelineTab.click();
    const clientSelect = page.getByTestId('pipeline-client');
    const selectedClient = await clientSelect.locator('.ant-select-selection-item').textContent().catch(() => '');
    if (!/Unique Identification Authority of India/i.test(selectedClient || '')) {
      await clientSelect.click();
      const visibleOption = page.locator('.ant-select-dropdown:not(.ant-select-dropdown-hidden) .ant-select-item-option').filter({ hasText: /Unique Identification Authority of India/i });
      await visibleOption.click();
    }
    await page.getByTestId('pipeline-library').waitFor();
    await page.waitForTimeout(1600);
    console.log(JSON.stringify({ selectedClient: await clientSelect.locator('.ant-select-selection-item').textContent().catch(() => ''), loadedClientId: await page.getByTestId('pipeline-library').getAttribute('data-loaded-client-id'), library: await page.getByTestId('pipeline-library').innerText() }));
    const targetPipeline = page.getByText(/UIDAI Candidate Hiring Journey/i).first();
    if (!await targetPipeline.count()) throw new Error(`Candidate pipeline missing. Library: ${await page.getByTestId('pipeline-library').innerText()}`);
    await targetPipeline.click();
    await page.getByTestId('pipeline-stage-flow').waitFor();

    const metadata = {
      name: await page.getByTestId('pipeline-name').inputValue(),
      code: await page.getByTestId('pipeline-code').inputValue(),
      scope: await page.getByTestId('pipeline-scope').locator('.ant-select-selection-item').textContent(),
      sla: await page.getByTestId('pipeline-sla-mode').locator('.ant-select-selection-item').textContent(),
      status: await page.locator('.form-builder-canvas > .ant-card').first().innerText(),
    };
    const cards = page.locator('.pipeline-stage-card');
    const stageCount = await cards.count();
    const stages = [];
    for (let index = 0; index < stageCount; index += 1) {
      const card = cards.nth(index);
      const cardText = await card.innerText();
      await card.getByRole('button', { name: 'Configure' }).click();
      const drawer = page.locator('.recruitment-pipeline-stage-drawer .ant-drawer-content');
      await drawer.waitFor();
      const automationHeader = await drawer.locator('.ant-collapse-header').filter({ hasText: 'Advanced automation' }).textContent().catch(() => '');
      const controls = await fieldSummary(drawer);
      const checkedLabels = await drawer.locator('.ant-checkbox-wrapper-checked').allTextContents();
      stages.push({ card: cardText.replace(/\s+/g, ' ').trim(), automation: automationHeader?.replace(/\s+/g, ' ').trim(), checked: checkedLabels.map(x => x.trim()), controls });
      await drawer.locator('.ant-drawer-close').click();
      await drawer.waitFor({ state: 'hidden' });
    }
    const transitions = await page.locator('.pipeline-transition-card').count();
    const validation = await page.getByTestId('pipeline-validation-error').textContent().catch(() => '');
    await page.screenshot({ path: '.codex-temp/candidate-pipeline-draft.png', fullPage: true });
    console.log(JSON.stringify({ metadata, stageCount, transitions, validation, failures, stages }, null, 2));
  } finally {
    await browser.close();
    sql(`UPDATE authsessions SET RevokedAt=UTC_TIMESTAMP() WHERE TokenHash='${tokenHash}' AND RevokedAt IS NULL;`);
  }
}

main().catch(error => { console.error(error.stack || error); process.exitCode = 1; });
