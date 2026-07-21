import EntityAttachmentPanel from './EntityAttachmentPanel'

export default function EmployeeAttachmentPanel({ employeeId, clientId, selfService = false }: { employeeId: number; clientId: number; selfService?: boolean }) {
  return <EntityAttachmentPanel
    entityType="EMPLOYEE"
    entityId={employeeId}
    clientId={clientId}
    moduleCode="EMPLOYEE"
    formCodes={['EMPLOYEE_CREATE_EDIT', 'EMPLOYEE_PROFILE']}
    title="Employee documents"
    selfService={selfService}
    emptyMessage="Save the employee first. Configured attachment fields will be available after Employee ID is generated."
  />
}
