import { useEffect, useMemo, useState } from 'react'
import { Button, Card, Checkbox, Col, Drawer, Form, Input, InputNumber, Row, Select, Space, Switch, Tabs, Tag } from 'antd'
import DataTable from './DataTable'
import SearchSelect, { selectOptions } from './SearchSelect'
import { getClients, getEmployees } from '../services/payrollService'
import { getDropdowns, getTravelExpenseSetup, getWorkLocations, saveTravelExpenseCategory, saveTravelPolicy, saveTravelPolicyAssignment, saveTravelPolicyRule } from '../services/settingsService'
import { getJson } from '../services/apiClient'
import type { Client, Drop, Employee, TravelExpenseCategory, TravelExpenseSetup, TravelPolicy, TravelPolicyAssignment, TravelPolicyRule, TravelPolicyRuleType, WorkLocation } from '../types/payroll'

type WorkflowOption = { id: number; name: string; workflowCode?: string; resourceType?: string; isActive?: boolean }
type DrawerMode = 'policy' | 'assignment' | 'rule' | 'category'
type CategoryMode = 'globalHeader' | 'globalCategory' | 'clientSetting'

const today = new Date().toISOString().slice(0, 10)
const ruleTypes: TravelPolicyRuleType[] = ['Travel Mode', 'Hotel', 'Meal', 'Per Diem', 'Local Conveyance', 'Travel Advance', 'Policy Violation']
const travelModes = ['Flight', 'Train', 'Bus', 'Taxi', 'Metro', 'Rental Car', 'Own Vehicle', 'Public Transport']
const hotelTypes = ['3 Star', '4 Star', '5 Star', 'Business Hotel', 'Guest House', 'Shared Accommodation']
const mealTypes = ['Breakfast', 'Lunch', 'Dinner', 'Snacks', 'Client Entertainment']
const perDiemTypes = ['Domestic Full Day', 'Domestic Half Day', 'Domestic Travel Day', 'International Full Day', 'International Half Day', 'International Travel Day', 'Non-working Day']
const conveyanceTypes = ['Taxi', 'Cab Aggregator', 'Fuel', 'Parking', 'Toll', 'Mileage', 'Public Transport']
const advanceTypes = ['Domestic Travel Advance', 'International Travel Advance', 'Project Advance', 'Emergency Advance']
const violationTypes = ['Hotel limit exceeded', 'Meal limit exceeded', 'Receipt missing', 'Expense exceeds allowed amount', 'Advance pending', 'Duplicate expense']
const policy0: TravelPolicy = { id: 0, policyCode: '', policyName: '', companyId: 0, companyName: '', businessUnit: '', effectiveFrom: today, effectiveTo: null, status: 'Draft', description: '', isActive: true }
const assignment0: TravelPolicyAssignment = { id: 0, policyId: 0, policyName: '', companyId: 0, companyName: '', branchId: null, branchName: '', department: '', grade: '', designation: '', employeeCategory: '', employmentType: '', employeeId: null, employeeName: '', priority: 100, effectiveFrom: today, effectiveTo: null, isActive: true }
const rule0: TravelPolicyRule = { id: 0, policyId: 0, policyName: '', ruleType: 'Travel Mode', ruleName: '', appliesTo: 'Flight', isAllowed: true, eligibilityJson: '{}', limitAmount: null, limitCurrency: 'INR', receiptMandatory: false, approvalRequired: false, workflowId: null, workflowName: '', exceptionHandling: 'Warning', configJson: '{}', isActive: true }
const category0: TravelExpenseCategory = { id: 0, clientId: 0, clientName: '', parentId: null, parentName: '', expenseType: '', isClaimHeader: true, categoryCode: '', categoryName: '', receiptMandatory: false, gstApplicable: false, dailyLimit: null, maximumClaim: null, requiresFinanceApproval: false, requiresManagerApproval: false, isActive: true }

export default function TravelExpensePolicySettings() {
  const [setup, setSetup] = useState<TravelExpenseSetup>({ policies: [], assignments: [], rules: [], categories: [], audit: [] })
  const [clients, setClients] = useState<Client[]>([]), [locations, setLocations] = useState<WorkLocation[]>([]), [employees, setEmployees] = useState<Employee[]>([]), [drops, setDrops] = useState<Drop[]>([])
  const [workflows, setWorkflows] = useState<WorkflowOption[]>([])
  const [drawer, setDrawer] = useState<DrawerMode | null>(null)
  const [policy, setPolicy] = useState<TravelPolicy>(policy0)
  const [assignment, setAssignment] = useState<TravelPolicyAssignment>(assignment0)
  const [rule, setRule] = useState<TravelPolicyRule>(rule0)
  const [category, setCategory] = useState<TravelExpenseCategory>(category0)
  const [categoryMode, setCategoryMode] = useState<CategoryMode>('clientSetting')
  const [activeRuleType, setActiveRuleType] = useState<TravelPolicyRuleType>('Travel Mode')

  const load = async () => {
    const [travelSetup, clientRows, locationRows, employeeRows, dropdownRows, workflowRows] = await Promise.all([
      getTravelExpenseSetup(),
      getClients(),
      getWorkLocations(),
      getEmployees(),
      getDropdowns(),
      getJson<WorkflowOption[]>('/api/workflows', [])
    ])
    setSetup(travelSetup)
    setClients(clientRows.filter(item => item.isActive))
    setLocations(locationRows.filter(item => item.isActive))
    setEmployees(employeeRows.filter(item => item.isActive))
    setDrops(dropdownRows.filter(item => item.isActive))
    setWorkflows(workflowRows.filter(item => item.isActive !== false))
  }

  useEffect(() => { void load() }, [])

  const departments = dropValues('Department', drops, employees.map(item => item.department))
  const designations = dropValues('Designation', drops, employees.map(item => item.designation))
  const grades = dropValues('Employee Grade', drops, employees.map(item => item.grade))
  const employmentTypes = dropValues('Employment Type', drops)
  const employeeCategories = dropValues('Employee Category', drops)
  const activePolicies = setup.policies.filter(item => item.isActive)
  const selectedPolicy = activePolicies.find(item => item.id === assignment.policyId || item.id === rule.policyId)
  const policyOptions = selectOptions(activePolicies.map(item => ({ value: item.id, label: `${item.policyName} - ${item.policyCode}` })), 'Select policy', 0)
  const companyLocations = locations.filter(item => item.clientId === assignment.companyId)
  const companyEmployees = employees.filter(item => item.clientId === assignment.companyId)
  const ruleRows = setup.rules.filter(item => item.ruleType === activeRuleType)
  const appliesToOptions = useMemo(() => optionsForRuleType(rule.ruleType), [rule.ruleType])
  const globalHeaders = uniqueById(setup.categories.filter(item => item.isClaimHeader))
  const globalCategories = uniqueById(setup.categories.filter(item => !item.isClaimHeader))
  const categoryHeaders = categoryMode === 'globalCategory' ? globalHeaders.filter(item => item.id !== category.id) : setup.categories.filter(item => item.clientId === category.clientId && item.isClaimHeader && item.id !== category.id)

  const openPolicy = (row?: TravelPolicy) => { setPolicy(row ? normalizePolicy(row) : policy0); setDrawer('policy') }
  const openAssignment = (row?: TravelPolicyAssignment) => { setAssignment(row ? normalizeAssignment(row) : { ...assignment0, policyId: activePolicies[0]?.id ?? 0, companyId: activePolicies[0]?.companyId ?? 0 }); setDrawer('assignment') }
  const openRule = (type: TravelPolicyRuleType, row?: TravelPolicyRule) => { setActiveRuleType(type); setRule(row ? { ...rule0, ...row, limitAmount: row.limitAmount ?? null, workflowId: row.workflowId ?? null } : { ...rule0, ruleType: type, appliesTo: optionsForRuleType(type)[0] ?? '', policyId: activePolicies[0]?.id ?? 0 }); setDrawer('rule') }
  const openCategory = (mode: CategoryMode, row?: TravelExpenseCategory) => {
    setCategoryMode(mode)
    setCategory(row ? { ...category0, ...row, parentId: row.parentId ?? null, dailyLimit: row.dailyLimit ?? null, maximumClaim: row.maximumClaim ?? null } : { ...category0, clientId: clients[0]?.id ?? 0, isClaimHeader: mode === 'globalHeader' })
    setDrawer('category')
  }
  const closeDrawer = () => setDrawer(null)

  const savePolicyRow = async () => { const response = await saveTravelPolicy(policy); if (response.ok) { closeDrawer(); await load() } }
  const saveAssignmentRow = async () => { const response = await saveTravelPolicyAssignment(assignment); if (response.ok) { closeDrawer(); await load() } }
  const saveRuleRow = async () => { const response = await saveTravelPolicyRule(rule); if (response.ok) { closeDrawer(); await load() } }
  const saveCategoryRow = async () => { const response = await saveTravelExpenseCategory(category); if (response.ok) { closeDrawer(); await load() } }

  return <section className="travel-policy-settings">
    <Card title="Travel & Expense Policy Configuration" size="small" className="settings-panel settings-table-panel">
      <Tabs items={[
        { key: 'policies', label: 'Policy Master', children: <><Header title="Travel policies" text="Company-wise effective-dated policy headers." action="Add policy" onClick={() => openPolicy()} /><DataTable rows={setup.policies} exportFileName="travel-policies" actions={row => <Button size="small" type="primary" onClick={() => openPolicy(row)}>Edit</Button>} columns={[
          { key: 'policyCode', label: 'Code' },
          { key: 'policyName', label: 'Policy' },
          { key: 'companyName', label: 'Company' },
          { key: 'businessUnit', label: 'Business unit' },
          { key: 'status', label: 'Status', render: row => <Tag color={row.status === 'Active' ? 'green' : row.status === 'Draft' ? 'blue' : 'default'}>{row.status}</Tag> },
          { key: 'effectiveFrom', label: 'From', value: row => dateText(row.effectiveFrom) },
          { key: 'effectiveTo', label: 'To', value: row => row.effectiveTo ? dateText(row.effectiveTo) : 'Open' }
        ]} /></> },
        { key: 'assignments', label: 'Assignment Rules', children: <><Header title="Policy assignment rules" text="Priority decides which policy applies when multiple rules match." action="Add assignment" onClick={() => openAssignment()} /><DataTable rows={setup.assignments} exportFileName="travel-policy-assignments" actions={row => <Button size="small" type="primary" onClick={() => openAssignment(row)}>Edit</Button>} columns={[
          { key: 'priority', label: 'Priority' },
          { key: 'policyName', label: 'Policy' },
          { key: 'companyName', label: 'Company' },
          { key: 'branchName', label: 'Branch', value: row => row.branchName || 'All' },
          { key: 'department', label: 'Department', value: row => row.department || 'All' },
          { key: 'designation', label: 'Designation', value: row => row.designation || 'All' },
          { key: 'employeeName', label: 'Employee', value: row => row.employeeName || 'All' },
          { key: 'isActive', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
        ]} /></> },
        { key: 'rules', label: 'Policy Rules', children: <><Tabs activeKey={activeRuleType} onChange={key => setActiveRuleType(key as TravelPolicyRuleType)} items={ruleTypes.map(type => ({ key: type, label: type, children: <><Header title={`${type} rules`} text="Limits, eligibility, approval requirement and exception handling." action={`Add ${type}`} onClick={() => openRule(type)} /><DataTable rows={ruleRows} exportFileName={`travel-${type.toLowerCase().replaceAll(' ', '-')}-rules`} actions={row => <Button size="small" type="primary" onClick={() => openRule(type, row)}>Edit</Button>} columns={[
          { key: 'policyName', label: 'Policy' },
          { key: 'ruleName', label: 'Rule' },
          { key: 'appliesTo', label: 'Applies to' },
          { key: 'isAllowed', label: 'Allowed', value: row => row.isAllowed ? 'Allowed' : 'Blocked' },
          { key: 'limitAmount', label: 'Limit', value: row => row.limitAmount == null ? '-' : `${row.limitCurrency} ${row.limitAmount}` },
          { key: 'approvalRequired', label: 'Approval', value: row => row.approvalRequired ? row.workflowName || 'Required' : 'No' },
          { key: 'exceptionHandling', label: 'Exception' }
        ]} /></> }))} /></> },
        { key: 'categories', label: 'Expense Categories', children: <Tabs items={[
          { key: 'globalHeaders', label: 'Global headers', children: <><Header title="Global expense headers" text="Create once, enable per client separately." action="Add header" onClick={() => openCategory('globalHeader')} /><DataTable rows={globalHeaders} exportFileName="global-expense-headers" actions={row => <Button size="small" type="primary" onClick={() => openCategory('globalHeader', row)}>Edit</Button>} columns={[
            { key: 'categoryCode', label: 'Code' },
            { key: 'categoryName', label: 'Header' },
            { key: 'isActive', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
          ]} /></> },
          { key: 'globalCategories', label: 'Global categories', children: <><Header title="Global line categories" text="Linked to global headers." action="Add category" onClick={() => openCategory('globalCategory')} /><DataTable rows={globalCategories} exportFileName="global-expense-categories" actions={row => <Button size="small" type="primary" onClick={() => openCategory('globalCategory', row)}>Edit</Button>} columns={[
            { key: 'parentName', label: 'Header' },
            { key: 'categoryCode', label: 'Code' },
            { key: 'categoryName', label: 'Category' },
            { key: 'isActive', label: 'Status', value: row => row.isActive ? 'Active' : 'Inactive' }
          ]} /></> },
          { key: 'clientEnablement', label: 'Client enablement', children: <><Header title="Client enablement and limits" text="Turn categories on/off and maintain limits per client." action="Enable / edit" onClick={() => openCategory('clientSetting')} /><DataTable rows={setup.categories} exportFileName="client-expense-enablements" actions={row => <Button size="small" type="primary" onClick={() => openCategory('clientSetting', row)}>Edit</Button>} columns={[
            { key: 'clientName', label: 'Client' },
            { key: 'expenseType', label: 'Expense type', value: row => row.expenseType || row.categoryName },
            { key: 'isClaimHeader', label: 'Level', render: row => <Tag color={row.isClaimHeader ? 'purple' : 'blue'}>{row.isClaimHeader ? 'Header' : 'Category'}</Tag> },
            { key: 'categoryCode', label: 'Code' },
            { key: 'categoryName', label: 'Name' },
            { key: 'receiptMandatory', label: 'Receipt', value: row => row.isClaimHeader ? '-' : row.receiptMandatory ? 'Mandatory' : 'Optional' },
            { key: 'dailyLimit', label: 'Daily limit', value: row => row.isClaimHeader ? '-' : row.dailyLimit ?? '-' },
            { key: 'maximumClaim', label: 'Max claim', value: row => row.isClaimHeader ? '-' : row.maximumClaim ?? '-' },
            { key: 'isActive', label: 'Enabled', value: row => row.isActive ? 'Yes' : 'No' }
          ]} /></> }
        ]} /> },
        { key: 'audit', label: 'Audit', children: <DataTable rows={setup.audit} exportFileName="travel-policy-audit" columns={[
          { key: 'changedOn', label: 'Changed on', value: row => row.changedOn ? new Date(row.changedOn).toLocaleString('en-IN') : '-' },
          { key: 'entityType', label: 'Entity' },
          { key: 'entityId', label: 'Reference' },
          { key: 'action', label: 'Action' },
          { key: 'changedBy', label: 'Changed by' }
        ]} /> }
      ]} />
    </Card>
    <Drawer className="settings-master-drawer travel-policy-drawer" title={drawerTitle(drawer)} open={drawer !== null} width="min(980px, 96vw)" destroyOnClose onClose={closeDrawer} footer={<Space><Button onClick={closeDrawer}>Cancel</Button><Button type="primary" onClick={() => drawer === 'policy' ? void savePolicyRow() : drawer === 'assignment' ? void saveAssignmentRow() : drawer === 'rule' ? void saveRuleRow() : void saveCategoryRow()}>Save</Button></Space>}>
      {drawer === 'policy' && <Form layout="vertical" requiredMark={false}><Row gutter={12}>
        <Col xs={24} md={8}><Form.Item label="Policy code"><Input value={policy.policyCode} onChange={event => setPolicy({ ...policy, policyCode: event.target.value.toUpperCase() })} /></Form.Item></Col>
        <Col xs={24} md={16}><Form.Item label="Policy name"><Input value={policy.policyName} onChange={event => setPolicy({ ...policy, policyName: event.target.value })} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Company"><SearchSelect value={policy.companyId} onChange={value => setPolicy({ ...policy, companyId: Number(value) })} options={selectOptions(clients.map(item => ({ value: item.id, label: item.name })), 'Select company', 0)} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Business unit"><Input value={policy.businessUnit} onChange={event => setPolicy({ ...policy, businessUnit: event.target.value })} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Status"><Select value={policy.status} onChange={value => setPolicy({ ...policy, status: value })} options={['Draft', 'Active', 'Inactive'].map(value => ({ value, label: value }))} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Effective from"><Input type="date" value={policy.effectiveFrom} onChange={event => setPolicy({ ...policy, effectiveFrom: event.target.value })} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Effective to"><Input type="date" value={policy.effectiveTo || ''} onChange={event => setPolicy({ ...policy, effectiveTo: event.target.value || null })} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Active"><Switch checked={policy.isActive} onChange={value => setPolicy({ ...policy, isActive: value })} /></Form.Item></Col>
        <Col xs={24}><Form.Item label="Description"><Input.TextArea rows={4} value={policy.description} onChange={event => setPolicy({ ...policy, description: event.target.value })} /></Form.Item></Col>
      </Row></Form>}
      {drawer === 'assignment' && <Form layout="vertical" requiredMark={false}><Row gutter={12}>
        <Col xs={24} md={12}><Form.Item label="Policy"><SearchSelect value={assignment.policyId} onChange={value => { const next = activePolicies.find(item => item.id === Number(value)); setAssignment({ ...assignment, policyId: Number(value), companyId: next?.companyId || assignment.companyId }) }} options={policyOptions} /></Form.Item></Col>
        <Col xs={24} md={12}><Form.Item label="Company"><SearchSelect value={assignment.companyId} onChange={value => setAssignment({ ...assignment, companyId: Number(value), branchId: null, employeeId: null })} options={selectOptions(clients.map(item => ({ value: item.id, label: item.name })), 'Policy company', selectedPolicy?.companyId ?? 0)} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Branch / location"><SearchSelect value={assignment.branchId || 0} onChange={value => setAssignment({ ...assignment, branchId: Number(value) || null })} options={selectOptions(companyLocations.map(item => ({ value: item.id, label: item.name })), 'All branches', 0)} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Department"><SearchSelect value={assignment.department} onChange={value => setAssignment({ ...assignment, department: String(value) })} options={[{ value: '', label: 'All departments' }, ...departments.map(value => ({ value, label: value }))]} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Designation"><SearchSelect value={assignment.designation} onChange={value => setAssignment({ ...assignment, designation: String(value) })} options={[{ value: '', label: 'All designations' }, ...designations.map(value => ({ value, label: value }))]} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Grade"><SearchSelect value={assignment.grade} onChange={value => setAssignment({ ...assignment, grade: String(value) })} options={[{ value: '', label: 'All grades' }, ...grades.map(value => ({ value, label: value }))]} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Employee category"><SearchSelect value={assignment.employeeCategory} onChange={value => setAssignment({ ...assignment, employeeCategory: String(value) })} options={[{ value: '', label: 'All categories' }, ...employeeCategories.map(value => ({ value, label: value }))]} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label="Employment type"><SearchSelect value={assignment.employmentType} onChange={value => setAssignment({ ...assignment, employmentType: String(value) })} options={[{ value: '', label: 'All types' }, ...employmentTypes.map(value => ({ value, label: value }))]} /></Form.Item></Col>
        <Col xs={24} md={12}><Form.Item label="Individual employee"><SearchSelect value={assignment.employeeId || 0} onChange={value => setAssignment({ ...assignment, employeeId: Number(value) || null })} options={selectOptions(companyEmployees.map(item => ({ value: item.id, label: `${item.employeeCode} - ${item.firstName} ${item.lastName}` })), 'All employees', 0)} /></Form.Item></Col>
        <Col xs={24} md={4}><Form.Item label="Priority"><InputNumber min={1} value={assignment.priority} onChange={value => setAssignment({ ...assignment, priority: Number(value || 100) })} /></Form.Item></Col>
        <Col xs={24} md={4}><Form.Item label="From"><Input type="date" value={assignment.effectiveFrom} onChange={event => setAssignment({ ...assignment, effectiveFrom: event.target.value })} /></Form.Item></Col>
        <Col xs={24} md={4}><Form.Item label="To"><Input type="date" value={assignment.effectiveTo || ''} onChange={event => setAssignment({ ...assignment, effectiveTo: event.target.value || null })} /></Form.Item></Col>
        <Col xs={24}><Checkbox checked={assignment.isActive} onChange={event => setAssignment({ ...assignment, isActive: event.target.checked })}>Active</Checkbox></Col>
      </Row></Form>}
      {drawer === 'rule' && <Form layout="vertical" requiredMark={false}><Row gutter={12}>
        <Col xs={24} md={10}><Form.Item label="Policy"><SearchSelect value={rule.policyId} onChange={value => setRule({ ...rule, policyId: Number(value) })} options={policyOptions} /></Form.Item></Col>
        <Col xs={24} md={7}><Form.Item label="Rule type"><Select value={rule.ruleType} onChange={value => setRule({ ...rule, ruleType: value, appliesTo: optionsForRuleType(value)[0] ?? '' })} options={ruleTypes.map(value => ({ value, label: value }))} /></Form.Item></Col>
        <Col xs={24} md={7}><Form.Item label="Applies to"><SearchSelect value={rule.appliesTo} onChange={value => setRule({ ...rule, appliesTo: String(value) })} options={appliesToOptions.map(value => ({ value, label: value }))} /></Form.Item></Col>
        <Col xs={24} md={12}><Form.Item label="Rule name"><Input value={rule.ruleName} onChange={event => setRule({ ...rule, ruleName: event.target.value })} /></Form.Item></Col>
        <Col xs={24} md={4}><Form.Item label="Allowed"><Switch checked={rule.isAllowed} onChange={value => setRule({ ...rule, isAllowed: value })} /></Form.Item></Col>
        <Col xs={24} md={4}><Form.Item label="Receipt"><Switch checked={rule.receiptMandatory} onChange={value => setRule({ ...rule, receiptMandatory: value })} /></Form.Item></Col>
        <Col xs={24} md={4}><Form.Item label="Active"><Switch checked={rule.isActive} onChange={value => setRule({ ...rule, isActive: value })} /></Form.Item></Col>
        <Col xs={24} md={6}><Form.Item label="Limit amount"><InputNumber min={0} value={rule.limitAmount ?? null} onChange={value => setRule({ ...rule, limitAmount: value == null ? null : Number(value) })} /></Form.Item></Col>
        <Col xs={24} md={6}><Form.Item label="Currency"><Input value={rule.limitCurrency} onChange={event => setRule({ ...rule, limitCurrency: event.target.value.toUpperCase() })} /></Form.Item></Col>
        <Col xs={24} md={6}><Form.Item label="Exception"><Select value={rule.exceptionHandling} onChange={value => setRule({ ...rule, exceptionHandling: value })} options={['Warning', 'Block', 'Approval Override'].map(value => ({ value, label: value }))} /></Form.Item></Col>
        <Col xs={24} md={6}><Form.Item label="Approval required"><Switch checked={rule.approvalRequired} onChange={value => setRule({ ...rule, approvalRequired: value })} /></Form.Item></Col>
        <Col xs={24} md={12}><Form.Item label="Workflow"><SearchSelect value={rule.workflowId || 0} onChange={value => setRule({ ...rule, workflowId: Number(value) || null })} options={selectOptions(workflows.map(item => ({ value: item.id, label: `${item.name}${item.workflowCode ? ` - ${item.workflowCode}` : ''}` })), 'No workflow', 0)} /></Form.Item></Col>
        <Col xs={24}><Form.Item label="Eligibility JSON"><Input.TextArea rows={4} value={rule.eligibilityJson} onChange={event => setRule({ ...rule, eligibilityJson: event.target.value })} /></Form.Item></Col>
        <Col xs={24}><Form.Item label="Additional config JSON"><Input.TextArea rows={5} value={rule.configJson} onChange={event => setRule({ ...rule, configJson: event.target.value })} placeholder={sampleConfig(rule.ruleType)} /></Form.Item></Col>
      </Row></Form>}
      {drawer === 'category' && <Form layout="vertical" requiredMark={false}><Row gutter={12}>
        {categoryMode === 'clientSetting' && <Col xs={24} md={8}><Form.Item label="Client"><SearchSelect value={category.clientId} onChange={value => setCategory({ ...category, clientId: Number(value) })} options={selectOptions(clients.map(item => ({ value: item.id, label: item.name })), 'Select client', 0)} /></Form.Item></Col>}
        <Col xs={24} md={8}><Form.Item label="Record type"><Select disabled value={categoryMode === 'globalHeader' || category.isClaimHeader ? 'Header' : 'Category'} options={[{ value: 'Header', label: 'Expense header' }, { value: 'Category', label: 'Line category' }]} /></Form.Item></Col>
        {(categoryMode === 'globalCategory' || (!category.isClaimHeader && categoryMode === 'clientSetting')) && <Col xs={24} md={8}><Form.Item label="Expense header"><SearchSelect disabled={categoryMode === 'clientSetting'} value={category.parentId || 0} onChange={value => { const parent = categoryHeaders.find(item => item.id === Number(value)); setCategory({ ...category, isClaimHeader: false, parentId: Number(value) || null, expenseType: parent?.expenseType || parent?.categoryName || category.expenseType, parentName: parent?.categoryName || '' }) }} options={selectOptions(categoryHeaders.map(item => ({ value: item.id, label: item.categoryName })), 'Select header', 0)} /></Form.Item></Col>}
        <Col xs={24} md={8}><Form.Item label="Code"><Input disabled={categoryMode === 'clientSetting'} value={category.categoryCode} onChange={event => setCategory({ ...category, categoryCode: event.target.value.toUpperCase().replace(/\s+/g, '_') })} /></Form.Item></Col>
        <Col xs={24} md={8}><Form.Item label={category.isClaimHeader ? 'Header name' : 'Category name'}><Input disabled={categoryMode === 'clientSetting'} value={category.categoryName} onChange={event => setCategory({ ...category, categoryName: event.target.value, expenseType: categoryMode === 'globalHeader' ? event.target.value : category.expenseType, isClaimHeader: categoryMode === 'globalHeader' })} /></Form.Item></Col>
        <Col xs={24} md={6}><Form.Item label="Daily limit"><InputNumber min={0} value={category.dailyLimit ?? null} onChange={value => setCategory({ ...category, dailyLimit: value == null ? null : Number(value) })} /></Form.Item></Col>
        <Col xs={24} md={6}><Form.Item label="Maximum claim"><InputNumber min={0} value={category.maximumClaim ?? null} onChange={value => setCategory({ ...category, maximumClaim: value == null ? null : Number(value) })} /></Form.Item></Col>
        <Col xs={24}><Space wrap><Checkbox checked={category.receiptMandatory} onChange={event => setCategory({ ...category, receiptMandatory: event.target.checked })}>Receipt mandatory</Checkbox><Checkbox checked={category.gstApplicable} onChange={event => setCategory({ ...category, gstApplicable: event.target.checked })}>GST applicable</Checkbox><Checkbox checked={category.requiresManagerApproval} onChange={event => setCategory({ ...category, requiresManagerApproval: event.target.checked })}>Manager approval</Checkbox><Checkbox checked={category.requiresFinanceApproval} onChange={event => setCategory({ ...category, requiresFinanceApproval: event.target.checked })}>Finance approval</Checkbox><Checkbox checked={category.isActive} onChange={event => setCategory({ ...category, isActive: event.target.checked })}>Active</Checkbox></Space></Col>
      </Row></Form>}
    </Drawer>
  </section>
}

function Header(p: { title: string; text: string; action: string; onClick: () => void }) {
  return <div className="component-table-head"><div><b>{p.title}</b><span>{p.text}</span></div><Space className="settings-master-actions"><Button type="primary" onClick={p.onClick}>{p.action}</Button></Space></div>
}

function dropValues(type: string, drops: Drop[], extra: string[] = []) {
  return Array.from(new Set([...drops.filter(item => item.type === type).map(item => item.value), ...extra].filter(Boolean))).sort((a, b) => a.localeCompare(b))
}

function uniqueById(rows: TravelExpenseCategory[]) {
  return Array.from(new Map(rows.map(item => [item.id, item])).values()).sort((a, b) => a.categoryName.localeCompare(b.categoryName))
}

function optionsForRuleType(type: TravelPolicyRuleType) {
  if (type === 'Hotel') return hotelTypes
  if (type === 'Meal') return mealTypes
  if (type === 'Per Diem') return perDiemTypes
  if (type === 'Local Conveyance') return conveyanceTypes
  if (type === 'Travel Advance') return advanceTypes
  if (type === 'Policy Violation') return violationTypes
  return travelModes
}

function drawerTitle(mode: DrawerMode | null) {
  if (mode === 'policy') return 'Travel policy'
  if (mode === 'assignment') return 'Policy assignment rule'
  if (mode === 'rule') return 'Policy rule'
  if (mode === 'category') return 'Expense category'
  return ''
}

function normalizePolicy(row: TravelPolicy): TravelPolicy {
  return { ...policy0, ...row, effectiveFrom: dateText(row.effectiveFrom), effectiveTo: row.effectiveTo ? dateText(row.effectiveTo) : null }
}

function normalizeAssignment(row: TravelPolicyAssignment): TravelPolicyAssignment {
  return { ...assignment0, ...row, branchId: row.branchId ?? null, employeeId: row.employeeId ?? null, effectiveFrom: dateText(row.effectiveFrom), effectiveTo: row.effectiveTo ? dateText(row.effectiveTo) : null }
}

function dateText(value: string) {
  return String(value || '').slice(0, 10)
}

function sampleConfig(type: TravelPolicyRuleType) {
  if (type === 'Hotel') return '{"cityLimits":{"Mumbai":5000},"countryLimits":{"India":5000},"sharedAccommodation":false}'
  if (type === 'Meal') return '{"fixedLimit":0,"dailyLimit":0,"clientEntertainment":false}'
  if (type === 'Per Diem') return '{"cityCategory":"Metro","country":"India","halfDay":0,"fullDay":0}'
  if (type === 'Local Conveyance') return '{"mileageRate":0,"dailyLimit":0}'
  if (type === 'Travel Advance') return '{"maximumAdvancePercent":80,"settlementDays":7,"recoveryRule":"Recover from salary"}'
  if (type === 'Policy Violation') return '{"message":"Policy violation detected","severity":"Warning"}'
  return '{"travelClass":"Economy","maximumFare":0}'
}
