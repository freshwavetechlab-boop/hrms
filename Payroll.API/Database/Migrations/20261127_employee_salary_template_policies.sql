CREATE TABLE IF NOT EXISTS employee_salary_template_policies (
    EmployeeId INT PRIMARY KEY,
    ClientId INT NOT NULL,
    SalaryStructureId VARCHAR(32) NOT NULL,
    AssignedBy VARCHAR(255) NOT NULL,
    AssignedAtUtc DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    INDEX IX_employee_template_policy (ClientId,SalaryStructureId),
    CONSTRAINT FK_employee_template_policy_employee FOREIGN KEY (EmployeeId) REFERENCES employees(Id) ON DELETE CASCADE
);
