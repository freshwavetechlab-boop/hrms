import WorkflowAdmin from '../components/WorkflowAdmin'
import WorkflowTasks from '../components/WorkflowTasks'
import DepartmentHeadAssignments from '../components/DepartmentHeadAssignments'
import WorkflowHistory from '../components/WorkflowHistory'
import type { workflowMenus } from '../data/payrollDefaults'
export type WorkflowMenu = (typeof workflowMenus)[number]
export default function WorkflowPage({ activeMenu }: { activeMenu: WorkflowMenu }) {
  if (activeMenu === 'Workflow Setup') return <WorkflowAdmin />
  if (activeMenu === 'Department Head Assignments') return <DepartmentHeadAssignments />
  if (activeMenu === 'My Tasks') return <WorkflowTasks />
  return <WorkflowHistory />
}
