# Reusable smart bulk import

The smart bulk-import UI is module-neutral. A module supplies a `BulkImportDefinition`; the mapper reads an arbitrary `.xlsx` or `.csv`, maps source column ordinals to canonical target fields, and produces a canonical workbook for that module's existing server-side import endpoint.

## Shared flow

1. Upload a standard template, or choose **Map any spreadsheet**.
2. Detect worksheets, headers and sample values in the browser.
3. Auto-map exact canonical names and configured aliases.
4. Let the user replace mappings by drag-and-drop or by selecting source then target.
5. Open **Preview uploaded Excel** to inspect every source column and data row. Each column header contains a searchable **Map** chip for direct target selection.
6. Keep mapped source columns, connector lines, target fields and final canonical-preview columns on the same colour identity.
7. Omit unmapped source columns.
8. Block review while a required target is unmapped.
9. Generate the module's canonical workbook.
10. Use the shared editable preview and paginated renderer.
11. Submit to the existing module endpoint, where all master-data and business validation still runs.

The browser mapping is not treated as server authorization or business validation. Employee imports still pass through the existing Employee import repository.

## Reusable files

- `payroll-ui/src/components/SmartBulkUploadMapper.tsx`
- `payroll-ui/src/components/SmartBulkSourcePreview.tsx`
- `payroll-ui/src/components/SmartBulkUploadMapper.css`
- `payroll-ui/src/utils/smartBulkImport.ts`
- `payroll-ui/src/components/BulkUploadPreviewModal.tsx`
- `payroll-ui/src/components/BulkUploadProgressModal.tsx`

## Module adapter

Employee fields and aliases are defined in:

- `payroll-ui/src/config/bulkImportDefinitions.ts`

To add another module, create another `BulkImportDefinition`, pass it to `SmartBulkUploadMapper`, and handle `onPrepared` by calling that module's existing import-preview/import flow. Target field codes and canonical headers must be allowlisted in the definition; never use uploaded header text as a database identifier.

## Employee behavior

- Existing HRMS template and multi-sheet Employee imports remain available.
- Arbitrary mapping generates a flat `Employees` sheet.
- `Employee Code` is required.
- `Portal Access` and `Active` default to `TRUE` when not mapped.
- `Change Reason` defaults to `Smart spreadsheet import`.
- Employee master validation, lookups, infotypes, upsert and login provisioning continue through the current API.

## Browser coverage

The guarded test is:

- `playwright-e2e/tests/employees/employee-smart-bulk-upload.spec.ts`

It is intentionally skipped unless `RUN_EMPLOYEE_BULK_MUTATIONS=1` is supplied. It verifies arbitrary headers, auto/manual mapping, connector colours, skipped columns, canonical preview, import request, completion and Employee Master visibility.
