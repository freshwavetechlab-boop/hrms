param(
    [string]$ClientCode = "PLRS",
    [string]$SettingsFile = "",
    [switch]$Execute
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $SettingsFile) {
    $devSettings = Join-Path $root "Payroll.API\appsettings.Development.json"
    $prodSettings = Join-Path $root "Payroll.API\appsettings.json"
    $SettingsFile = if (Test-Path $devSettings) { $devSettings } else { $prodSettings }
}

if (-not (Test-Path $SettingsFile)) {
    throw "Settings file not found: $SettingsFile"
}

$settings = Get-Content $SettingsFile -Raw | ConvertFrom-Json
$connectionString = $settings.ConnectionStrings.Default
if (-not $connectionString) {
    throw "ConnectionStrings:Default not found in $SettingsFile"
}

$tempRoot = Join-Path $env:TEMP "frevo-password-reset-from-aadhaar"
if (Test-Path $tempRoot) { Remove-Item $tempRoot -Recurse -Force }
New-Item -ItemType Directory -Path $tempRoot | Out-Null

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="MySqlConnector" Version="2.6.0" />
  </ItemGroup>
</Project>
"@ | Set-Content -Path (Join-Path $tempRoot "ResetPasswords.csproj") -Encoding UTF8

@'
using System.Security.Cryptography;
using MySqlConnector;

const int SaltBytes = 16;
const int HashBytes = 32;
const int Iterations = 120_000;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: <connectionString> <clientCode> <execute:true|false>");
    return 2;
}

var connectionString = args[0];
var clientCode = args[1];
var execute = bool.Parse(args[2]);

await using var connection = new MySqlConnection(connectionString);
await connection.OpenAsync();

var rows = new List<Row>();
await using (var command = connection.CreateCommand())
{
    command.CommandText = @"
SELECT e.Id EmployeeId,
       e.EmployeeCode,
       COALESCE(u.Id, 0) UserId,
       COALESCE(JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.aadhaarNumber')),
                JSON_UNQUOTE(JSON_EXTRACT(e.PersonalJson, '$.AadhaarNumber')),
                pd.AadhaarNumber,
                '') AadhaarNumber
FROM employees e
JOIN clients c ON c.Id = e.ClientId
JOIN authusers u ON u.EmployeeId = e.Id
LEFT JOIN employeepersonaldetails pd ON pd.EmployeeId = e.Id
WHERE (c.Code = @clientCode OR c.Name LIKE CONCAT('%', @clientCode, '%'))
  AND e.IsActive = TRUE
ORDER BY e.EmployeeCode;";
    command.Parameters.AddWithValue("@clientCode", clientCode);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        rows.Add(new Row(
            reader.GetInt32("EmployeeId"),
            reader.GetString("EmployeeCode"),
            reader.GetInt32("UserId"),
            CleanPasswordValue(reader.GetString("AadhaarNumber"))));
    }
}

var missing = rows.Where(row => row.AadhaarNumber.Length < 8).ToList();
var valid = rows.Where(row => row.AadhaarNumber.Length >= 8).ToList();

Console.WriteLine($"Client: {clientCode}");
Console.WriteLine($"Users linked to employees: {rows.Count}");
Console.WriteLine($"Ready for password reset: {valid.Count}");
Console.WriteLine($"Skipped because Aadhaar is missing/less than 8 chars: {missing.Count}");

if (missing.Count > 0)
{
    Console.WriteLine("Skipped employees:");
    foreach (var row in missing.Take(30))
        Console.WriteLine($"  {row.EmployeeCode} (EmployeeId {row.EmployeeId})");
    if (missing.Count > 30) Console.WriteLine($"  ... {missing.Count - 30} more");
}

if (!execute)
{
    Console.WriteLine("DRY RUN only. Re-run with -Execute in PowerShell to update PasswordHash.");
    return 0;
}

await using var transaction = await connection.BeginTransactionAsync();
var updated = 0;
foreach (var row in valid)
{
    await using var update = connection.CreateCommand();
    update.Transaction = transaction;
    update.CommandText = @"
UPDATE authusers
SET PasswordHash = @passwordHash,
    MustChangePassword = TRUE,
    IsActive = TRUE
WHERE Id = @userId;";
    update.Parameters.AddWithValue("@passwordHash", HashPassword(row.AadhaarNumber));
    update.Parameters.AddWithValue("@userId", row.UserId);
    updated += await update.ExecuteNonQueryAsync();
}
await transaction.CommitAsync();

Console.WriteLine($"Password reset completed. Updated users: {updated}");
Console.WriteLine("Login ID remains employee code. Temporary password is corrected Aadhaar number. User will be forced to change password on first login.");
return 0;

static string CleanPasswordValue(string value) =>
    new((value ?? string.Empty).Trim().Where(char.IsLetterOrDigit).ToArray());

static string HashPassword(string password)
{
    var salt = RandomNumberGenerator.GetBytes(SaltBytes);
    var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
    return $"PBKDF2-SHA256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
}

internal sealed record Row(int EmployeeId, string EmployeeCode, int UserId, string AadhaarNumber);
'@ | Set-Content -Path (Join-Path $tempRoot "Program.cs") -Encoding UTF8

Write-Host "Using settings: $SettingsFile"
Write-Host "Building temporary reset utility..."
dotnet run --project $tempRoot -- "$connectionString" "$ClientCode" "$($Execute.IsPresent.ToString().ToLowerInvariant())"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
