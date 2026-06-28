-- Seed RECL salary deduction components from RECL DATA.xlsx / Salary sheet.
-- Run after 2026-06-28_normalize_non_statutory_json.sql.
-- Employee PF already exists as PF. This adds missing employee-side deductions.

USE payroll;

INSERT INTO SalaryComponents
(Id, Code, ComponentType, Category, Name, PayType, CalculationType, ValueText, Formula, BaseComponent, Taxable, Ctc, ProRata, Fbp, RestrictFbp, Epf, Esi, Recurring, Scheduled, InvestmentType, CorrectionOf, Active, Priority)
VALUES
(110, 'ESIC', 'Employee State Insurance', 'Deduction', 'Employee ESIC', 'Fixed Pay', 'Manual / Variable', '', '', '', FALSE, FALSE, FALSE, FALSE, FALSE, 'Never', FALSE, TRUE, FALSE, '', '', TRUE, 120),
(111, 'PT_LWF_WC', 'Professional Tax / LWF / Workmen Comp', 'Deduction', 'PT / LWF / Workmen Comp', 'Fixed Pay', 'Manual / Variable', '', '', '', FALSE, FALSE, FALSE, FALSE, FALSE, 'Never', FALSE, TRUE, FALSE, '', '', TRUE, 130),
(112, 'TDS', 'Tax Deducted at Source', 'Deduction', 'TDS', 'Fixed Pay', 'Manual / Variable', '', '', '', FALSE, FALSE, FALSE, FALSE, FALSE, 'Never', FALSE, TRUE, FALSE, '', '', TRUE, 140),
(113, 'RECOVERY', 'Recovery', 'Deduction', 'Recovery', 'Variable Pay', 'Manual / Variable', '', '', '', FALSE, FALSE, FALSE, FALSE, FALSE, 'Never', FALSE, FALSE, TRUE, '', '', TRUE, 150)
ON DUPLICATE KEY UPDATE
Code = VALUES(Code),
ComponentType = VALUES(ComponentType),
Category = VALUES(Category),
Name = VALUES(Name),
PayType = VALUES(PayType),
CalculationType = VALUES(CalculationType),
ValueText = VALUES(ValueText),
Formula = VALUES(Formula),
BaseComponent = VALUES(BaseComponent),
Taxable = VALUES(Taxable),
Ctc = VALUES(Ctc),
ProRata = VALUES(ProRata),
Fbp = VALUES(Fbp),
RestrictFbp = VALUES(RestrictFbp),
Epf = VALUES(Epf),
Esi = VALUES(Esi),
Recurring = VALUES(Recurring),
Scheduled = VALUES(Scheduled),
InvestmentType = VALUES(InvestmentType),
CorrectionOf = VALUES(CorrectionOf),
Active = VALUES(Active),
Priority = VALUES(Priority);

INSERT INTO SalaryStructureLines (StructureId, ComponentId, ValueText, SortOrder)
SELECT s.Id, CAST(c.Id AS CHAR), '', c.Priority
FROM SalaryStructures s
JOIN SalaryComponents c ON c.Code IN ('ESIC', 'PT_LWF_WC', 'TDS', 'RECOVERY')
WHERE NOT EXISTS (
  SELECT 1 FROM SalaryStructureLines l
  WHERE l.StructureId = s.Id AND l.ComponentId = CAST(c.Id AS CHAR)
);
