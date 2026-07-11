# Client Billing Advanced Costing Configuration

## Purpose

Client Billing Advanced Costing is an optional layer used when the customer wants billing or payroll-cost reports to be calculated from processed payroll output.

It does not change salary components, salary templates, payroll calculation, payslips, or statutory payroll processing. It only reads the payroll result and applies client-specific commercial rules for reporting.

Use this when a client contract says things like:

- recover the exact amount paid to employees;
- add employer statutory cost separately;
- add PF admin / EDLI / ESIC recovery;
- add insurance or other fixed charges;
- add service charge / commission;
- apply GST on billable lines;
- produce a billing basis or payroll-cost report.

## What Is Hardcoded And What Is Configurable

The RECL values are not hardcoded in code. They are saved as configuration in these tables:

- `client_billing_settings`
- `client_billing_cost_rule_headers`
- `client_billing_cost_rule_lines`

The engine has only generic supported rule behavior in code:

- supported line types;
- supported base types;
- supported rate types;
- calculation order: normal billable lines, then commission/service-charge lines, then GST;
- matching against processed payroll result.

Client-specific values like RECL, 3.26%, PF admin, EDLI, GST, effective dates, and whether a line is included in service-charge base are configurable.

## Where To Configure

Open:

`Settings > Client Billing Configuration`

Use:

- `Enable module` for normal billing setup.
- `Enable advanced costing` only when payroll-output based costing is required.
- `Add rule line` / `Edit` to maintain calculation lines.
- `Configuration guide` to open the in-app help popup before maintaining rules.

The old Billing Rate Cards screen is not required for this advanced payroll-output based billing model. It is intentionally not part of the main configuration flow now. Existing data is not deleted, but new client billing should be maintained through Advanced Costing Rules.

## Quick Mental Model

Think of every billing rule line as answering three questions.

### 1. Line Type: What should the system pick or create?

Examples:

- pick total net pay;
- pick employer PF;
- pick employer ESIC;
- pick exact component code;
- add insurance fixed amount;
- calculate service charge.

### 2. Match Value: Which exact source should be matched?

This is the most important field.

If `Line Type = Statutory Type`, then `Match Value` must match the `Statutory type` maintained in Salary Components.

Example:

Salary Component:

| Component Code | Name | Statutory Type |
|---|---|---|
| EPF_ER | Employer PF | PF Employer |

Billing Rule:

| Field | Value |
|---|---|
| Line Type | Statutory Type |
| Match Value | PF Employer |

Result:

The system finds payroll result lines generated from salary components tagged as `PF Employer`. In this example, it picks `EPF_ER`.

### 3. Base Type: Which amount should be used?

Examples:

- `Processed Amount`: use the matched payroll line amount;
- `Net Pay`: use employee net payable;
- `Gross Pay`: use gross salary;
- `Billable Salary`: use accumulated lines marked as service-charge base.

For statutory lines like PF Employer or ESI Employer, normally use:

`Base Type = Processed Amount`

For service charge, normally use:

`Base Type = Billable Salary`

## Salary Component Linking

Advanced billing does not calculate PF/ESI/TDS by itself. Payroll calculates those amounts first using salary components and payroll rules. Advanced billing then reads the processed payroll result.

The link is:

```text
Salary Component
  -> Statutory Type field
  -> Payroll result line
  -> Advanced Billing Line Type = Statutory Type
  -> Match Value = same statutory type
  -> Billing report value
```

Example for RECL Employer PF:

```text
Salary Component EPF_ER
  -> Statutory Type = PF Employer
  -> Payroll run calculates EPF_ER amount
  -> Billing rule has Line Type = Statutory Type
  -> Match Value = PF Employer
  -> Report picks EPF_ER processed amount
```

This is why `Match Value` should not be random text when using `Statutory Type`. It must match the statutory identity configured in Salary Components.

## Configuration Tables

### `client_billing_settings`

Controls whether client billing is enabled.

Important fields:

- `ClientId`: client for which billing is enabled.
- `IsEnabled`: enables Client Billing Configuration.
- `AdvancedCostingEnabled`: enables advanced payroll-output based costing.

### `client_billing_cost_rule_headers`

This is the contract header.

Important fields:

- `ClientId`: client for this rule.
- `WorkLocationId`: optional. Blank means all work locations.
- `RuleName`: business-readable rule name.
- `EffectiveFrom`: first date from which rule applies.
- `EffectiveTo`: optional end date.
- `GstRatePercent`: GST rate to apply on taxable billing lines.
- `IsActive`: inactive rules are ignored.

Rule selection:

- The report finds an active rule for the client and pay period.
- If a location-specific rule exists, use it for that location.
- If location is blank, it applies to all locations of the client.

### `client_billing_cost_rule_lines`

Each row defines one billable calculation line.

Important fields:

- `HeaderId`: links line to the rule header.
- `LineType`: what kind of value this row is reading or creating.
- `MatchValue`: what to match in payroll result or display as a fixed/commission line.
- `BaseType`: which amount base should be used.
- `RateType`: how amount should be calculated.
- `RateValue`: percentage or fixed value.
- `TaxApplicable`: whether GST applies on this line.
- `CommissionApplicable`: whether this line should be included in service-charge base.
- `DisplayGroup`: grouping label in report.
- `SortOrder`: calculation/display order.
- `IsActive`: inactive lines are ignored.

## Supported Line Types

Line type answers this question:

`What is this billing line trying to read or create?`

Use the line type first, then choose the base type and rate type.

### Base Amount

Uses a payroll-level total instead of a component.

Common base types:

- `Net Pay`: amount actually payable to employee.
- `Gross Pay`: gross earning amount.
- `Employer Cost`: payroll employer cost if available.
- `Billable Salary`: accumulated billable base from prior lines.

Example:

`Base Amount + Net Pay + Actual`

This means bill the exact employee net payable.

Best used for:

- total paid to employee;
- gross salary basis;
- employer cost basis where available.

Do not use this for PF, ESI, TDS, or individual salary components.

### Payroll Component

Matches a specific salary component code.

Example:

`Payroll Component + EPF_ER + Actual`

This means pick processed amount of component `EPF_ER`.

Best used when:

- the contract refers to an exact component code;
- the same statutory identity has multiple components and only one code should be billed;
- you want to bill a custom client-specific component.

Example:

| Field | Value |
|---|---|
| Line Type | Payroll Component |
| Match Value | EPF_ER |
| Base Type | Processed Amount |
| Rate Type | Actual |

Avoid this when the same business meaning can be identified by statutory type. For PF/ESI/PT/TDS reporting, `Statutory Type` is usually cleaner.

### Component Category

Matches a broad component category from payroll result.

Examples:

- `Earning`
- `Deduction`
- `Benefit`
- `Reimbursement`

Use this only if the client contract is based on a category total.

Best used when:

- all earnings are billable;
- all reimbursements are billable;
- a contract says "all benefits" or "all deductions" are part of a cost basis.

Example:

| Field | Value |
|---|---|
| Line Type | Component Category |
| Match Value | Reimbursement |
| Base Type | Processed Amount |
| Rate Type | Actual |

Be careful: this is broad. If one earning is not billable, prefer component-level or statutory-level rules.

### Statutory Type

Matches canonical statutory identity from salary component configuration.

Examples:

- `PF Employer`
- `ESI Employer`
- `PF Employee`
- `TDS`
- `Professional Tax`

This is better than component code when component code may differ by client but statutory identity is same.

Best used for:

- PF employee / employer;
- ESI employee / employer;
- professional tax;
- TDS;
- labour welfare fund;
- NPS or other statutory identities.

Example:

| Field | Value |
|---|---|
| Line Type | Statutory Type |
| Match Value | PF Employer |
| Base Type | Processed Amount |
| Rate Type | Actual |

This works only when salary components are correctly tagged with statutory type in Salary Components.

### Fixed Charge

Creates a fixed charge or zero-value placeholder.

Example:

`Fixed Charge + Insurance + Fixed + 0`

This keeps insurance visible in the report even when current value is zero.

Best used for:

- insurance charge;
- fixed administration fee;
- one fixed monthly charge;
- placeholder line currently configured as zero.

Example:

| Field | Value |
|---|---|
| Line Type | Fixed Charge |
| Match Value | Insurance |
| Base Type | Billable Salary |
| Rate Type | Fixed |
| Rate Value | 0 |

For fixed charge, the base type is mostly informational unless a future percent/fixed behavior depends on it.

### Commission

Calculates service charge or commission.

Example:

`Commission + Service Charges + Billable Salary + Percent + 3.26`

This means calculate 3.26% on the accumulated billable salary base.

Best used for:

- service charge;
- management fee;
- commission;
- markup on selected billable lines.

Example:

| Field | Value |
|---|---|
| Line Type | Commission |
| Match Value | Service Charges |
| Base Type | Billable Salary |
| Rate Type | Percent |
| Rate Value | 3.26 |

For Commission, keep `CommissionApplicable = No` on the commission line itself. Otherwise commission may become part of its own base in future calculations.

## Line Type Decision Guide

| Requirement | Recommended Line Type | Match Value Example | Base Type |
|---|---|---|---|
| Bill exact net paid to employee | Base Amount | Total Paid To Employee | Net Pay |
| Bill gross salary | Base Amount | Gross Salary | Gross Pay |
| Bill exact component code | Payroll Component | EPF_ER | Processed Amount |
| Bill all reimbursements | Component Category | Reimbursement | Processed Amount |
| Bill employer PF | Statutory Type | PF Employer | Processed Amount |
| Bill employer ESIC | Statutory Type | ESI Employer | Processed Amount |
| Bill insurance as fixed value | Fixed Charge | Insurance | Billable Salary |
| Add service charge on selected lines | Commission | Service Charges | Billable Salary |

## Supported Rate Types

### Actual

Use the processed payroll amount as-is.

### Percent

Apply percentage on the selected base.

Example:

`RateValue = 3.26` means 3.26%.

### Fixed

Use `RateValue` as a fixed amount.

## Supported Base Types

Base type answers this question:

`Which amount should this line calculate from?`

### Processed Amount

Use the matching processed payroll component/category/statutory amount.

Use with:

- Payroll Component;
- Component Category;
- Statutory Type.

Example:

Employer PF line:

- Line Type = `Statutory Type`
- Match Value = `PF Employer`
- Base Type = `Processed Amount`
- Rate Type = `Actual`

The engine finds the processed employer PF amount and uses it directly.

### Net Pay

Use employee net payable from the payrun.

Use with:

- Base Amount.

Example:

Total paid to employee:

- Line Type = `Base Amount`
- Base Type = `Net Pay`
- Rate Type = `Actual`

This is useful when client has to reimburse the amount actually payable to the employee.

### Gross Pay

Use employee gross pay from the payrun.

Use with:

- Base Amount.

Example:

Gross salary billing:

- Line Type = `Base Amount`
- Base Type = `Gross Pay`
- Rate Type = `Actual`

This is useful when client billing is based on total earnings before deductions.

### Employer Cost

Use employer-cost total if available in processed payroll output.

Use this only when the payrun/report output has a reliable employer-cost total. If employer cost needs custom contract logic, configure separate statutory/fixed lines instead.

### Billable Salary

Use the accumulated amount from prior lines marked as `CommissionApplicable`.

This is usually used for service charge / commission.

Example:

For RECL, these lines are marked `CommissionApplicable = Yes`:

- Gross Pay / employee salary cost
- Employer PF
- PF Admin
- EDLI
- Employer ESIC
- Insurance

Then Service Charges uses:

- Line Type = `Commission`
- Base Type = `Billable Salary`
- Rate Type = `Percent`
- Rate Value = `3.26`

So service charge is calculated on the selected billable base, not blindly on all payroll values.

## Base Type Decision Guide

| Requirement | Recommended Base Type | Notes |
|---|---|---|
| Use exact matched component/statutory/category amount | Processed Amount | Best for PF, ESI, reimbursements, component code |
| Use amount payable to employee | Net Pay | Best for "total paid to employee" billing |
| Use gross earnings | Gross Pay | Best for gross salary billing |
| Use system employer-cost total | Employer Cost | Use only if payroll output supports it clearly |
| Use accumulated selected billable lines | Billable Salary | Best for service charge / commission |

## Common Mistakes

### Using Payroll Component Instead Of Statutory Type

If you configure PF recovery with component code `EPF_ER`, it will work only if that exact component is present.

If you configure it with:

- Line Type = `Statutory Type`
- Match Value = `PF Employer`

then it works as long as the salary component is tagged correctly, even if component code changes later.

### Using Billable Salary Too Early

`Billable Salary` is calculated from prior lines where `CommissionApplicable = Yes`.

If you use `Billable Salary` before those lines are calculated, the value may be zero or incomplete.

Keep service charge lines at a later sort order.

### Forgetting CommissionApplicable

If a line should be included in service charge base, mark:

`CommissionApplicable = Yes`

If this is missed, the line amount will appear in billing but service charge will not be calculated on it.

### Forgetting TaxApplicable

If GST should apply on a line, mark:

`TaxApplicable = Yes`

GST rate comes from the header.

## Calculation Flow

1. The report reads the selected payrun.
2. It finds processed employee-wise payroll lines.
3. It finds the active advanced billing rule for the client, location, and pay period.
4. It calculates normal lines first:
   - Base Amount
   - Payroll Component
   - Component Category
   - Statutory Type
   - Fixed Charge
5. If a line has `CommissionApplicable = Yes`, its amount is added to the service-charge base.
6. It calculates `Commission` lines using the selected base.
7. GST is calculated on lines where `TaxApplicable = Yes`.
8. Final invoice basis is:

`Billing amount before GST + GST amount`

## Current RECL Configuration

Client:

`Rural Electrification Corporation (REC) Ltd. / RECL`

Rule:

`RECL - Salary cost + employer statutory + insurance + service charge`

Effective from:

`01-04-2026`

Work location:

`All locations`

GST:

`18%`

### RECL Rule Lines

| Order | Line Type | Match Value | Base Type | Rate Type | Rate | GST | Service-Charge Base | Purpose |
|---:|---|---|---|---|---:|---|---|---|
| 10 | Base Amount | Total Employee Salary Cost | Gross Pay | Actual | 0 | Yes | Yes | Recover employee salary cost before employee-side statutory deductions |
| 20 | Statutory Type | PF Employer | Processed Amount | Actual | 0 | Yes | Yes | Recover employer PF |
| 30 | Statutory Type | PF Employer | Processed Amount | Percent | 4.1666667 | Yes | Yes | PF admin charge |
| 40 | Statutory Type | PF Employer | Processed Amount | Percent | 4.1666667 | Yes | Yes | EDLI charge |
| 50 | Statutory Type | ESI Employer | Processed Amount | Actual | 0 | Yes | Yes | Recover employer ESIC |
| 60 | Fixed Charge | Insurance | Billable Salary | Fixed | 923.73 | Yes | Yes | Insurance charge |
| 70 | Commission | Service Charges | Billable Salary | Percent | 3.26 | Yes | No | Service charge |

### PF Admin And EDLI Logic

Requirement:

PF admin and EDLI should be calculated as:

`Employer PF / 12% * 0.5%`

This is mathematically:

`Employer PF * 4.1666667%`

That is why PF Admin and EDLI are configured as:

- `LineType = Statutory Type`
- `MatchValue = PF Employer`
- `RateType = Percent`
- `RateValue = 4.1666667`

Example:

If Employer PF is `1800`:

- PF wage base = `1800 / 12% = 15000`
- PF admin = `15000 * 0.5% = 75`
- EDLI = `15000 * 0.5% = 75`

The configured percent also gives:

`1800 * 4.1666667% = 75`

## How To Configure A New Client

### Step 1: Enable Billing

Open:

`Settings > Client Billing Configuration`

Select the client and enable:

- Client Billing
- Advanced Costing, only if required

### Step 2: Create Header

Create a rule header with:

- Client
- Work location, or blank for all locations
- Rule name
- Effective from date
- Optional effective to date
- GST rate
- Active = Yes

Use one header per different commercial contract.

Examples:

- One rule for all locations.
- Separate rule for each work location if commercial terms differ.
- New header from future effective date when contract changes.

### Step 3: Decide Billing Base

Choose one starting base:

- Use `Net Pay` if client reimburses exact employee payable.
- Use `Gross Pay` if client contract is based on gross earnings.
- Use component/category lines if only selected components are billable.

### Step 4: Add Employer Statutory Lines

If employer statutory cost is recoverable, add lines like:

- `Statutory Type = PF Employer`
- `Statutory Type = ESI Employer`

Use `Actual` when exact processed payroll amount should be recovered.

### Step 5: Add Extra Charges

For PF admin / EDLI:

- Use the statutory base line as source.
- Use `Percent`.
- Use the agreed percentage.

For fixed insurance:

- Use `Fixed Charge`.
- Use `Fixed`.
- Enter monthly amount, or zero if currently not charged.

### Step 6: Add Service Charge

Use:

- `LineType = Commission`
- `BaseType = Billable Salary`
- `RateType = Percent`
- `RateValue = agreed service charge`

Make sure all lines that should be included in service-charge base have:

`CommissionApplicable = Yes`

Keep the commission line itself as:

`CommissionApplicable = No`

### Step 7: Validate Report

Open:

`Reports > Client Billing Report > Payrun Billing Basis`

or

`Reports > Client Billing Report > Payroll Cost Report`

Select:

- Client
- Payrun or month

Check:

- employee net/gross base;
- employer statutory;
- PF admin / EDLI;
- insurance;
- service charge;
- GST;
- final invoice basis.

## Common Configuration Patterns

### Pattern 1: Salary Only Plus GST

Use when client only pays salary value and GST.

Lines:

- Base Amount / Net Pay / Actual / GST Yes / Commission base No

### Pattern 2: Salary Cost Plus Employer Statutory Plus Service Charge

Use when client pays employee salary cost, employer statutory cost, then service charge.

Lines:

- Base Amount / Gross Pay / Actual / Commission base Yes
- Statutory Type / PF Employer / Actual / Commission base Yes
- Statutory Type / ESI Employer / Actual / Commission base Yes
- Commission / Service Charges / Billable Salary / Percent

### Pattern 3: Gross Pay Plus Reimbursements

Use when contract bills gross salary and reimbursements separately.

Lines:

- Base Amount / Gross Pay / Actual
- Component Category / Reimbursement / Actual

### Pattern 4: Employer Cost Report Only

Use for internal payroll-cost view, not client invoice basis.

Lines:

- Base Amount / Gross Pay / Actual
- Statutory Type / PF Employer / Actual
- Statutory Type / ESI Employer / Actual
- Fixed Charge / Insurance / Fixed

## Troubleshooting

### Amount Is Zero

Check:

- payroll was processed for selected employee/payrun;
- component exists in payrun result;
- salary component has correct statutory type;
- rule effective date covers the pay period;
- selected client/location matches rule header;
- line is active.

### Employer PF Is Zero

Check salary component setup:

- Employer PF component exists.
- It has statutory type `PF Employer`.
- It is included in the employee pay group/template.
- Payroll was run after component setup was corrected.

If old payrun result does not contain employer PF, rerun payroll for a test payrun.

### Service Charge Looks Low

Check `CommissionApplicable`.

Only lines marked as `CommissionApplicable = Yes` are included in `Billable Salary` base.

### GST Is Not Applied

Check:

- header `GstRatePercent`;
- line `TaxApplicable = Yes`.

### Billing Config Changed, Do I Need To Rerun Payroll?

No, not normally.

Advanced billing reads existing processed payroll output. If payroll output already has the needed components, changing billing configuration is enough.

Rerun payroll only when the required payroll component/statutory value was missing or wrong in the payrun result.

## Guardrails

- Do not change salary components only for billing needs.
- Keep billing logic in Client Billing Advanced Costing.
- Use statutory type matching where possible instead of component code matching.
- Use new effective-dated header when contract changes.
- Keep old headers active only until their valid end date.
- Test with one payrun before using the report for billing.
