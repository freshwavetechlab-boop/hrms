import type { NotificationRule, NotificationSetup, NotificationSmtpSetting, NotificationTemplate } from '../types/payroll'
import { getJson, postEmpty, postJson } from './apiClient'

export const getNotificationSetup = () => getJson<NotificationSetup>('/api/notifications/setup', { smtp: { id: 1, isEnabled: false, host: '', port: 587, userName: '', password: '', enableSsl: true, fromEmail: '', fromName: '' }, templates: [], rules: [], queue: [], logs: [] })
export const saveNotificationSmtp = (smtp: NotificationSmtpSetting) => postJson('/api/notifications/smtp', smtp, smtp, { successMessage: 'SMTP settings saved.' })
export const saveNotificationTemplate = (template: NotificationTemplate) => postJson('/api/notifications/templates', template, template, { successMessage: 'Email template saved.' })
export const saveNotificationRule = (rule: NotificationRule) => postJson('/api/notifications/rules', rule, rule, { successMessage: 'Notification rule saved.' })
export const retryNotification = (id: number) => postEmpty(`/api/notifications/queue/${id}/retry`, null, { successMessage: 'Notification queued for retry.' })
export const sendNotificationTest = (ruleId: number, toEmail: string) => postJson('/api/notifications/test', { ruleId, toEmail }, null, { successMessage: 'Test notification queued.' })
