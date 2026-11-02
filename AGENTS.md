# Frevo HRMS project guidance

## Learn from Excel import incidents

For payroll Excel import, suggested-mapping, formula/source, duplicate-row, reconciliation or payslip-display incidents, apply the owner's `frevo-excel-import-learning` skill when available. Its local entrypoint is `C:/Users/APPC0024/.codex/skills/frevo-excel-import-learning/SKILL.md`.

Do not finish a confirmed import fix without retaining its reusable, anonymized pattern in `Payroll.API/FrevoPilot/docs/excel-payroll-mapping.md` where relevant to mapping, plus regression coverage for the actual failing layer. Verify retrieval of new mapping cases and keep the knowledge version consistent. The maintained workflow is in `docs/excel-payslips.md` under **Learning from import incidents**. Preserve original salary amounts/formulas and existing functionality; never promote an unverified AI suggestion or a one-off row approval into a global rule.

## Learn from resume parsing incidents

For confirmed resume parser/AI-review incidents, apply `frevo-resume-learning` when available at `C:/Users/APPC0024/.codex/skills/frevo-resume-learning/SKILL.md`. Keep a source-verified, anonymized regression and submit the checked professional pattern through the existing dedicated LLM learning gateway. Preserve shared behavior across published, bulk, pipeline and replacement entrypoints. Raw AI output is never a verified training label; distinguish accepted examples from actual evaluated/activated weight adapters.
