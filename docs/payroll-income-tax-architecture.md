# Payroll Income Tax Engine Architecture

## Overview

The income tax module is structured into four layers:

1. Global statutory rules: Government of India income tax rules, maintained only by System/Super Admin.
2. Company income tax settings: client-wise activity windows and workflow controls.
3. Employee tax activities: regime selection, IT declaration, POI submission and projection.
4. Tax calculation engine: data-driven computation with audit snapshots.

The refactor reuses the existing tax module, services, ESS tax flow and normalized activity/submission tables. It does not create a parallel module.

## Global Statutory Rules

Statutory data is global and versioned by financial year through:

- `tax_financial_years`
- `tax_rule_versions`
- `tax_regimes`
- `tax_slabs`
- `tax_surcharges`
- `tax_final_adjustments`
- `tax_declaration_sections`
- `tax_deduction_options`
- `tax_standard_deductions`
- `tax_rebate_rules`
- `tax_exemption_rules`
- `tax_hra_rules`
- `tax_rule_source_references`

Company payroll users do not maintain these records. APIs for slabs, surcharges, declaration sections and final adjustments require `tax.statutory.manage`.

## Company Settings

Company settings are maintained by client and financial year in `tax_client_settings`, with normalized windows in `tax_activity_windows`.

Company users manage only:

- IT declaration release and lock dates
- POI submission release and lock dates
- Reminder email settings
- Payroll month for approved POI
- Employee regime selection permission
- Default tax regime

Statutory slabs, deductions, cess, rebate, HRA and surcharge are not company settings.

## Employee Activities

Employee activity data is preserved and normalized into:

- `employee_tax_regime_selections`
- `employee_tax_declaration_headers`
- `employee_tax_declaration_lines`
- `employee_tax_declarations`
- `employee_tax_declaration_proofs`

Available declaration sections come from active statutory declaration sections for the selected financial year and regime. Employees can submit only when the relevant company activity window is open and within lock dates.

## Calculation Engine

`TaxEngineRepository.ComputeAsync` reads:

- active financial year rule version
- selected or default employee regime
- standard deduction
- approved POI deductions
- slabs
- rebate rules
- surcharge thresholds
- configurable final adjustments such as Health & Education Cess
- employee salary and TDS already deducted

The calculation stores every result in `tax_computation_snapshots` with applied inputs, rule version, rules and timestamp. Tax rules are data-driven and not hardcoded in calculation logic.

## Annual Update Process

For a new financial year:

1. Create a new `tax_financial_years` record.
2. Create a new active `tax_rule_versions` record.
3. Copy prior year statutory records where applicable.
4. Edit slabs, surcharge, rebate, deduction sections, HRA and final adjustment records as per the Finance Act or CBDT circular.
5. Attach source references for traceability.
6. Configure company activity windows separately for each client.

Old payroll calculations continue pointing to the original rule version and snapshot.

## Migration Strategy

Existing company tax settings remain in `tax_client_settings` and are mirrored into `tax_activity_windows`.

Existing employee declarations are preserved in `employee_tax_declarations` and migrated into declaration headers and lines for IT Declaration and POI activities.

Legacy statutory columns such as slab-level cess and surcharge are removed from slabs. Cess and similar final additions are maintained in `tax_final_adjustments`; surcharge is maintained in `tax_surcharges`.

## Assumptions

- Current statutory seed is an initial operational dataset and must be reviewed by finance before production use.
- Company settings intentionally retain some legacy columns for backward compatibility, but the UI exposes only the ERP-grade workflow settings.
- Future extensibility should add dedicated statutory maintenance screens for financial years, rule versions, rebate, HRA and source references instead of adding company-level statutory fields.
