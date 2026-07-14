param(
    [string]$Password = "Test@12345"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "tools/RecruitmentTestDataSeeder/RecruitmentTestDataSeeder.csproj"
dotnet run --project $project -- "--password=$Password"
