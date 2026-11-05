# Selected-client user access

An unrestricted security administrator can open **Security > Users > Add / Edit**, choose **Client access: Selected clients**, select several **Assigned clients**, and save the user's roles. Roles control the available actions; client assignments restrict the records those actions can use. This mode is for standalone backoffice users, without an employee link.

The user gets one **Assigned client** selector in the shell header. It contains only active assigned clients, with no All clients option. Changing it clears record selections and reloads the workspace. The choice survives refresh in that user's browser session. A request operates on one assigned client; it does not aggregate all assignments.

## Existing modes and the Excel exception

| Mode | Stored identity | Business scope |
| --- | --- | --- |
| All clients | `ClientAccessMode=Legacy`, `ClientId=null` | Existing unrestricted behavior, subject to role permissions |
| Single client | `ClientAccessMode=Legacy`, positive `ClientId` | Existing single-client behavior |
| Selected clients | `ClientAccessMode=Selected`, `ClientId=null`, `AssignedClientIds` | One active assigned client per request |

Excel Payslips and Excel Payroll Setup remain available only to an unrestricted administrator with their existing workspace permissions. Selected-client and single-client users cannot open their routes or APIs. The Excel Payslips component, import/mapping, source amounts, calculations and exports are unchanged.

Shared statutory/organization definitions remain readable where a scoped workflow needs them. Editing global or multi-client definitions, security administration, infrastructure, global talent-pool actions and unsafe unscoped configuration imports requires an unrestricted administrator. Unknown resource routes are denied for Selected users until their authoritative ownership mapping is implemented.

## API behavior

Authenticated Selected requests send `X-Client-Id`. A native download can supply the same assigned `clientId` in its URL. Missing, conflicting, unassigned or explicit zero contexts are rejected before business handlers. Optional empty client fields in submissions are assigned the validated active context, never interpreted as a global creation request.

The middleware resolves record and foreign-key ownership from the database (or the existing owned import-job cache), including downloads, employee selections, workflows, attachments, recruitment and payroll records. Fixed-resource lookups are grouped and cached within each request, avoiding one database call per employee in bulk attendance/payroll submissions. It projects the effective client into existing single-client handlers. Scoped setup writes preserve foreign/shared rows; aggregate settings, billing, travel, statutory settings and dashboard approvals have explicit client projections. Personal history responses are also filtered for revoked/foreign client records.

Assignments are reloaded with every authenticated request. Removing/deactivating a client cannot silently turn Selected access into All access. An empty Selected assignment stops business requests. Add new resource routes and foreign-key aliases to `SelectedClientOwnershipCatalog`, with ownership regression coverage, before allowing them for this mode.

## Schema and release order

The narrow, repeatable migration adds `authusers.ClientAccessMode` with default `Legacy`, plus `authuser_clients` and its foreign keys. It does not seed or modify users, roles, permissions, client assignments or business data. Core `clients` and `authusers` tables must already exist.

Using the intended environment's existing configuration, run the newly built API artifact's migration before serving the new API:

```powershell
dotnet Payroll.API.dll --migrate-client-access
```

Release the API before the UI. Selected account saves use the dedicated `/api/security/users/client-access` capability endpoint, and the UI disables Selected assignment when the authenticated API payload lacks the new capability fields. Existing Legacy saves keep the original endpoint.

**Rollback:** do not run an older API against active Selected accounts. An older API understands null `ClientId` as All clients. Before reverting the API, disable those accounts and revoke their sessions, or deliberately convert each account to an appropriate supported scope. Retain the additive schema; dropping it is unnecessary.

## Verification

Focused backend tests cover ownership/context rejection, record and download IDs, nested employee references, shared definitions, setup preservation, scoped approvals, attachment configuration, import-job polling and Legacy/Excel access behavior. Actual MySQL fixture checks cover migration idempotence, login/session assignment hydration/revocation, last-admin protection and transactional scoped settings preservation. A separate real MySQL batch probe checked 641 synthetic employees in one employee SELECT, plus foreign/missing/global ownership, joined resources, numeric/text identifier behavior and request-local cache refresh. The API also passed isolated Development startup/DI validation without a real database connection.

Frontend tests and a production build pass. Isolated browser checks exercise Selected creation, assigned-only switching, reload/navigation, inherited admin-grant restrictions, Excel route exclusion, and unchanged Legacy Single/All flows. Fixture users and clients are synthetic; these checks do not create production users or verify a production deployment.
