/*
  Purpose:
    Correct Aadhaar, UAN, ESIC, phone/mobile and bank account for employees uploaded under PLSR, then keep the
    employee master JSON/detail/active IT0002 records in sync.

  How to use:
    1. Take DB backup first.
    2. Run this script up to the staging table creation.
    3. Load/insert corrected data into employee_identifier_corrections_staging.
       Required columns: employee_code, aadhaar_number, uan_number, esic_number, phone, bank_ac.
    4. Run the preview SELECT statements.
    5. If preview is correct, run the transaction block.

  Important:
    PasswordHash cannot be safely fixed in pure SQL because the app uses
    PBKDF2-SHA256 with a random salt. After this SQL, run:
      powershell -ExecutionPolicy Bypass -File .\scripts\reset-client-passwords-from-aadhaar.ps1 -ClientCode PLSR -Execute
*/

DROP TABLE IF EXISTS employee_identifier_corrections_staging;
CREATE TABLE employee_identifier_corrections_staging (
    employee_code VARCHAR(80) NOT NULL,
    aadhaar_number VARCHAR(40) NOT NULL DEFAULT '',
    uan_number VARCHAR(40) NOT NULL DEFAULT '',
    esic_number VARCHAR(40) NOT NULL DEFAULT '',
    phone VARCHAR(40) NOT NULL DEFAULT '',
    bank_ac VARCHAR(80) NOT NULL DEFAULT '',
    remarks VARCHAR(500) NOT NULL DEFAULT '',
    PRIMARY KEY (employee_code)
);

-- Example manual entry. Replace with your actual corrected rows or import a CSV.
-- INSERT INTO employee_identifier_corrections_staging (employee_code, aadhaar_number, uan_number, esic_number, phone, bank_ac)
-- VALUES
-- ('PLSR001', '123456789012', '100000000001', 'ESIC001', '9876543210', '1234567890123456');

-- Preview 1: rows that will be matched for PLSR.
SELECT
    e.Id AS employee_id,
    e.EmployeeCode AS employee_code,
    CONCAT(e.FirstName, ' ', e.LastName) AS employee_name,
    COALESCE(JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.aadhaarNumber')), '') AS current_aadhaar_json,
    COALESCE(pd.AadhaarNumber, '') AS current_aadhaar_detail,
    s.aadhaar_number AS corrected_aadhaar,
    COALESCE(JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.uanNumber')), '') AS current_uan_json,
    COALESCE(pd.UanNumber, '') AS current_uan_detail,
    s.uan_number AS corrected_uan,
    COALESCE(pd.EsicNumber, '') AS current_esic_detail,
    s.esic_number AS corrected_esic,
    COALESCE(pd.Mobile, '') AS current_phone_detail,
    s.phone AS corrected_phone,
    COALESCE(pay.BankAccountNo, '') AS current_bank_ac,
    s.bank_ac AS corrected_bank_ac
FROM employees e
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId = e.Id
LEFT JOIN employeepaymentdetails pay ON pay.EmployeeId = e.Id
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%')
ORDER BY e.EmployeeCode;

-- Preview 2: staging rows that do not match an active PLSR employee.
SELECT s.*
FROM employee_identifier_corrections_staging s
LEFT JOIN employees e ON e.EmployeeCode = s.employee_code
LEFT JOIN clients c ON c.Id = e.ClientId
WHERE e.Id IS NULL OR NOT (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%');

START TRANSACTION;

-- Ensure detail rows exist.
INSERT INTO employeepersonaldetails (EmployeeId, AadhaarNumber, UanNumber, EsicNumber, Mobile)
SELECT e.Id, '', '', '', ''
FROM employees e
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId = e.Id
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%')
  AND pd.EmployeeId IS NULL;

INSERT INTO employeepaymentdetails (EmployeeId, BankAccountNo)
SELECT e.Id, ''
FROM employees e
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
LEFT JOIN employeepaymentdetails pay ON pay.EmployeeId = e.Id
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%')
  AND pay.EmployeeId IS NULL;

-- Audit old vs new values before correcting.
INSERT INTO employee_audit_trail (EmployeeId, EmployeeCode, ActionType, InfotypeCode, FieldName, OldValue, NewValue, EffectiveFrom, ChangedBy)
SELECT e.Id, e.EmployeeCode, 'Data Correction', '0002', 'AadhaarNumber',
       COALESCE(pd.AadhaarNumber, ''),
       s.aadhaar_number,
       CURDATE(),
       'identifier-correction-script'
FROM employees e
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId = e.Id
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%')
  AND COALESCE(pd.AadhaarNumber, '') <> s.aadhaar_number;

INSERT INTO employee_audit_trail (EmployeeId, EmployeeCode, ActionType, InfotypeCode, FieldName, OldValue, NewValue, EffectiveFrom, ChangedBy)
SELECT e.Id, e.EmployeeCode, 'Data Correction', '0002', 'UanNumber',
       COALESCE(pd.UanNumber, ''),
       s.uan_number,
       CURDATE(),
       'identifier-correction-script'
FROM employees e
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId = e.Id
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%')
  AND COALESCE(pd.UanNumber, '') <> s.uan_number;

INSERT INTO employee_audit_trail (EmployeeId, EmployeeCode, ActionType, InfotypeCode, FieldName, OldValue, NewValue, EffectiveFrom, ChangedBy)
SELECT e.Id, e.EmployeeCode, 'Data Correction', '0002', 'EsicNumber',
       COALESCE(pd.EsicNumber, ''),
       s.esic_number,
       CURDATE(),
       'identifier-correction-script'
FROM employees e
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId = e.Id
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%')
  AND COALESCE(pd.EsicNumber, '') <> s.esic_number;

INSERT INTO employee_audit_trail (EmployeeId, EmployeeCode, ActionType, InfotypeCode, FieldName, OldValue, NewValue, EffectiveFrom, ChangedBy)
SELECT e.Id, e.EmployeeCode, 'Data Correction', '0002', 'Mobile',
       COALESCE(pd.Mobile, ''),
       s.phone,
       CURDATE(),
       'identifier-correction-script'
FROM employees e
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId = e.Id
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%')
  AND COALESCE(pd.Mobile, '') <> s.phone;

INSERT INTO employee_audit_trail (EmployeeId, EmployeeCode, ActionType, InfotypeCode, FieldName, OldValue, NewValue, EffectiveFrom, ChangedBy)
SELECT e.Id, e.EmployeeCode, 'Data Correction', '0009', 'BankAccountNo',
       COALESCE(pay.BankAccountNo, ''),
       s.bank_ac,
       CURDATE(),
       'identifier-correction-script'
FROM employees e
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
LEFT JOIN employeepaymentdetails pay ON pay.EmployeeId = e.Id
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%')
  AND COALESCE(pay.BankAccountNo, '') <> s.bank_ac;

-- Correct normalized personal detail table.
UPDATE employeepersonaldetails pd
JOIN employees e ON e.Id = pd.EmployeeId
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
SET
    pd.AadhaarNumber = s.aadhaar_number,
    pd.UanNumber = s.uan_number,
    pd.EsicNumber = s.esic_number,
    pd.Mobile = s.phone
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%');

-- Correct normalized payment detail table.
UPDATE employeepaymentdetails pay
JOIN employees e ON e.Id = pay.EmployeeId
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
SET pay.BankAccountNo = s.bank_ac
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%');

-- Keep linked user mobile aligned with corrected phone.
UPDATE authusers u
JOIN employees e ON e.Id = u.EmployeeId
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
SET u.Mobile = s.phone
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%');

-- Correct employee master JSON used by many screens and login provisioning.
UPDATE employees e
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
SET e.PersonalJson = JSON_SET(
    COALESCE(e.PersonalJson, JSON_OBJECT()),
    '$.aadhaarNumber', s.aadhaar_number,
    '$.AadhaarNumber', s.aadhaar_number,
    '$.uanNumber', s.uan_number,
    '$.UanNumber', s.uan_number,
    '$.esicNumber', s.esic_number,
    '$.EsicNumber', s.esic_number,
    '$.mobile', s.phone,
    '$.Mobile', s.phone
)
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%');

-- Correct employee payment JSON used by older screens/import paths.
UPDATE employees e
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
SET e.PaymentJson = JSON_SET(
    COALESCE(e.PaymentJson, JSON_OBJECT()),
    '$.bankAccountNo', s.bank_ac,
    '$.BankAccountNo', s.bank_ac
)
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%');

-- Correct active personal infotype history record, if it exists.
UPDATE employee_it0002_personal_data it2
JOIN employees e ON e.Id = it2.EmployeeId
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
SET
    it2.AadhaarNumber = s.aadhaar_number,
    it2.UanNumber = s.uan_number,
    it2.EsicNumber = s.esic_number,
    it2.Mobile = s.phone,
    it2.ChangeReason = 'Identifier correction after bulk upload'
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%')
  AND it2.Status = 'Active';

-- Correct active bank infotype history record, if it exists.
UPDATE employee_it0009_bank_details it9
JOIN employees e ON e.Id = it9.EmployeeId
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
SET
    it9.BankAccountNo = s.bank_ac,
    it9.ChangeReason = 'Bank account correction after bulk upload'
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%')
  AND it9.Status = 'Active';

COMMIT;

-- Final verification.
SELECT
    e.EmployeeCode AS employee_code,
    CONCAT(e.FirstName, ' ', e.LastName) AS employee_name,
    pd.AadhaarNumber AS aadhaar_detail,
    JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.aadhaarNumber')) AS aadhaar_json,
    pd.UanNumber AS uan_detail,
    JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.uanNumber')) AS uan_json,
    pd.EsicNumber AS esic_detail,
    JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.esicNumber')) AS esic_json,
    pd.Mobile AS phone_detail,
    JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.mobile')) AS phone_json,
    pay.BankAccountNo AS bank_ac_detail,
    JSON_UNQUOTE(JSON_EXTRACT(e.PaymentJson, '$.bankAccountNo')) AS bank_ac_json,
    u.Id AS user_id,
    u.Email AS login_id,
    u.Mobile AS user_mobile,
    u.MustChangePassword AS must_change_password
FROM employees e
JOIN clients c ON c.Id = e.ClientId
JOIN employee_identifier_corrections_staging s ON s.employee_code = e.EmployeeCode
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId = e.Id
LEFT JOIN employeepaymentdetails pay ON pay.EmployeeId = e.Id
LEFT JOIN authusers u ON u.EmployeeId = e.Id
WHERE (c.Code = 'PLSR' OR c.Name LIKE '%PLSR%')
ORDER BY e.EmployeeCode;
