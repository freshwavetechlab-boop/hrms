USE payroll;

INSERT INTO DropdownMasters (Type, Value, IsActive)
SELECT DISTINCT 'State', TRIM(State), TRUE
FROM WorkLocations
WHERE TRIM(COALESCE(State, '')) <> ''
  AND NOT EXISTS (
    SELECT 1 FROM DropdownMasters d
    WHERE d.Type = 'State' AND d.Value = TRIM(WorkLocations.State)
  );

INSERT INTO DropdownMasters (Type, Value, IsActive)
SELECT DISTINCT CONCAT('City:', TRIM(State)), TRIM(City), TRUE
FROM WorkLocations
WHERE TRIM(COALESCE(State, '')) <> ''
  AND TRIM(COALESCE(City, '')) <> ''
  AND NOT EXISTS (
    SELECT 1 FROM DropdownMasters d
    WHERE d.Type = CONCAT('City:', TRIM(WorkLocations.State))
      AND d.Value = TRIM(WorkLocations.City)
  );

INSERT INTO DropdownMasters (Type, Value, IsActive)
SELECT DISTINCT 'State', TRIM(State), TRUE
FROM Organizations
WHERE TRIM(COALESCE(State, '')) <> ''
  AND NOT EXISTS (
    SELECT 1 FROM DropdownMasters d
    WHERE d.Type = 'State' AND d.Value = TRIM(Organizations.State)
  );

INSERT INTO DropdownMasters (Type, Value, IsActive)
SELECT DISTINCT CONCAT('City:', TRIM(State)), TRIM(City), TRUE
FROM Organizations
WHERE TRIM(COALESCE(State, '')) <> ''
  AND TRIM(COALESCE(City, '')) <> ''
  AND NOT EXISTS (
    SELECT 1 FROM DropdownMasters d
    WHERE d.Type = CONCAT('City:', TRIM(Organizations.State))
      AND d.Value = TRIM(Organizations.City)
  );
