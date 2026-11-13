import test from 'node:test'
import assert from 'node:assert/strict'
import fs from 'node:fs'
import ts from 'typescript'
const source = fs.readFileSync(new URL('../src/utils/excelPayslipDelivery.ts', import.meta.url), 'utf8')
const { canQueuePayslipEmail, canQueueCorrectedPayslipEmail, correctedPayslipResendBlockReason, payslipSentCopyLabel } = await import(`data:text/javascript;base64,${Buffer.from(ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText).toString('base64')}`)
test('coordinator copies do not block a different personal recipient but cannot be repeated across modes', () => {
  const status = { status: 'Pending', canSend: true, recipientDeliveries: [{ mode: 'Combined', email: 'coordinator@example.test', status: 'Sent' }] }
  assert.equal(canQueuePayslipEmail(status, 'Individual', 'employee@example.test'), true)
  assert.equal(canQueuePayslipEmail(status, 'Combined', ' COORDINATOR@example.test '), false)
  assert.equal(canQueuePayslipEmail(status, 'Individual', 'coordinator@example.test'), false)
  assert.equal(canQueuePayslipEmail({ ...status, canSend: false }, 'Individual', 'different@example.test'), false)
  assert.equal(canQueuePayslipEmail({ ...status, recipientDeliveries: [{ mode: 'Unknown', email: '' }] }, 'Combined', 'new@example.test'), false)
  assert.equal(canQueuePayslipEmail({ ...status, recipientDeliveries: [{ mode: 'Combined', email: 'old@example.test', status: 'Unknown' }] }, 'Individual', 'new@example.test'), false)
})
test('a mailbox Sent copy is never inferred from SMTP acceptance or a blank legacy field', () => {
  assert.equal(payslipSentCopyLabel('Saved'), 'Saved in Sent folder')
  assert.equal(payslipSentCopyLabel(undefined), 'Sent copy not verified')
  assert.match(payslipSentCopyLabel('Failed'), /failed/)
  assert.match(payslipSentCopyLabel('NotConfigured'), /IMAP off/)
})

test('corrected resend accepts completed or unsent recipients without changing ordinary skip-sent behavior', () => {
  for (const state of ['Sent', 'Delivered', 'Failed', 'Bounced', 'Pending']) {
    const status = { status: state, canSend: state === 'Pending', recipientDeliveries: [{ mode: 'Individual', status: state, email: 'employee@example.test' }] }
    assert.equal(canQueueCorrectedPayslipEmail(status), true)
    assert.equal(correctedPayslipResendBlockReason(status), '')
    assert.equal(canQueuePayslipEmail(status, 'Individual', 'employee@example.test'), false)
  }
  assert.equal(canQueueCorrectedPayslipEmail(undefined), false)
})

test('corrected resend never resolves active or uncertain delivery by choosing a new recipient address', () => {
  for (const state of ['Queued', 'Unknown', 'Deferred']) {
    assert.equal(canQueueCorrectedPayslipEmail({ status: state, canSend: false }), false)
    assert.equal(canQueueCorrectedPayslipEmail({ status: 'Sent', canSend: false, recipientDeliveries: [{ mode: 'Individual', status: state, email: 'employee@example.test' }] }), false)
  }
  assert.equal(canQueueCorrectedPayslipEmail({ status: 'Pending', canSend: true, recipientDeliveries: [{ mode: 'Unknown', status: 'Sent', email: '' }] }), false)
  assert.match(correctedPayslipResendBlockReason({ status: 'Queued' }), /already queued/)
  assert.match(correctedPayslipResendBlockReason({ status: 'Unknown' }), /uncertain/)
  assert.match(correctedPayslipResendBlockReason({ status: 'Deferred' }), /delayed/)
})

test('a separate queued or delayed coordinator copy does not block corrected individual delivery', () => {
  for (const status of ['Queued', 'Deferred']) {
    assert.equal(canQueueCorrectedPayslipEmail({ status: 'Sent', canSend: false, recipientDeliveries: [{ mode: 'Combined', status, email: 'coordinator@example.test' }] }), true)
  }
  assert.equal(canQueueCorrectedPayslipEmail({ status: 'Sent', canSend: false, recipientDeliveries: [{ mode: 'Combined', status: 'Unknown', email: 'coordinator@example.test' }] }), false)
})
