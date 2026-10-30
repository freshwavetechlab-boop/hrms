# Native conditional formulas and CL/LWP salary impact

## Saved batch is the onboarding source

The sequence is **raw workbook → Excel Payslip capture/review → saved batch/version → Transfer from this saved batch → reviewed components/templates/employee setup**. Supporting original/dependency files are retained through the existing attachment service. Transfer never parses an independent raw file or accepts an uploaded workbook as its master input.

`ReadSourceAsync` selects the client-scoped saved batch and its authoritative calculation source. It rejects a stored JSON identity that differs from the selected client/version. Apply locks target state and the source batch, rebuilds the plan and checks the full reviewed hash. That hash includes source batch/source snapshots, mappings, request, identities and configuration. Formula review hashes include captured cell and original workbook/mapping provenance. Existing request receipts, keep-existing choices and preview freshness checks remain in use.

## Main payroll AST

`PayrollFormulaEvaluator` replaces the former eager numeric parser inside the main `PayRunRepository.EvaluateFormula`. Both Template simulation and normal calculation use this path. It supports bounded numeric/boolean arithmetic/comparisons, lazy IF and IFERROR, AND/OR, MIN/MAX/SUM/ABS and ROUND/ROUNDUP/ROUNDDOWN. IFERROR catches arithmetic value errors only; missing native variables and unsupported syntax/functions remain actionable structural errors.

Rounding supports explicit integer precision -6 through 8. ROUND uses ties away from zero; ROUNDUP moves away from zero and ROUNDDOWN truncates toward zero, including negative values. Existing one-argument rounding means precision zero. This deliberately corrects the former negative ROUNDUP/ROUNDDOWN ceiling/floor behavior. Component-level separate rounding settings have not been redefined.

Limits: 4096 characters, 64 nesting depth, 512 atoms and 256 function arguments. No eval, workbook links, filesystem/network access or scripting. This native numeric/boolean subset does not support text-dependent rules, date/string functions or VLOOKUP execution.

Transfer proof now translates bounded numeric conditional structures with existing reviewed component/day inputs and validates both configured and translated expressions with the native AST. It does not substitute cached lookup outputs as recurring rules. Same-period native attendance/LWP mapping now has explicit saved-batch source-seed and existing-native proof modes; see [attendance proof](excel-native-attendance-proof.md). Unbound lookup/cross-sheet/shared references remain blocked. Saved Employee simulation and full component/net/employer parity are still separate acceptance steps.

The salary-template UI can request main-engine verification even when its older browser-only preview cannot interpret the new conditional syntax. The server remains the calculation authority; local preview limitations do not bypass server validation.

## CL/LWP salary-impact panel

Attendance policy simulator → **Salary amount impact — main payroll engine**:

1. Explicitly load saved client template snapshots through `/api/payroll-calculation/template-snapshots`. This route uses existing simulation permissions/client scope and an explicit read-only transaction. It projects salary component/template metadata only, without employee rows or personal identifiers.
2. Choose the exact template ID/fingerprint and review its rules, annual CTC if used, optional temporary monthly overrides, source divisor/payable/actual-present days and scenario inputs. Unknown worked days never default to payable days.
3. The panel rechecks the saved snapshot hash, then calls the existing `/simulate` Template contract twice with the same immutable snapshot. The only client-side arithmetic is subtraction of returned component/earnings/deduction/employer/net amounts.
4. Changed inputs mark previous results stale. Changed template hash, missing inputs, permission/calculator errors or differing component classifications prevent a valid comparison. No amount is estimated by scaling net pay.

Coverage is **Template snapshot arithmetic**, not Employee payroll certification. Employee overrides, approved adjustments, dated PF/PT/TDS and effective assignment history require the existing Employee simulation workflow. A source/scenario divisor that cannot accept the supplied payable/present units is unresolved rather than silently coerced. Template IDs are currently shown with fingerprints; this catalogue does not yet project friendly template names.

Simulation does not publish policy, grant entitlement, consume CL, alter balances/attendance/masters, create a payrun or send notifications. Existing draft storage remains separate and user/client scoped. Real policy activation still requires reviewed human parameters and an explicit future action.

## Tests and remaining verification

`ExcelNativeConditionalTests` compares synthetic source Excel and native AST results, lazy errors, negative rounding, conditional transfer structure, actual main-calculator totals and CL/LWP day deltas. `tools/test-cl-lwp-salary-impact.mjs` tests transport/delta behavior using in-memory API doubles, hash/scope changes, unknown inputs and error recovery. No actual employee rows, original file uploads or live DB writes are used.

Mocked desktop/mobile interactions were exercised with the owner-started frontend; see local-browser-validation.md. Real template catalogue DB integration remains unverified. No migration or deployment has been applied.
