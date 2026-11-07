param([Parameter(Mandatory=$true)][string]$Archive)
$ErrorActionPreference = 'Stop'
$scratch = Join-Path $env:TEMP ('cadai-package-test-' + [guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $Archive -DestinationPath $scratch
$destination = Join-Path $scratch 'must-not-install'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scratch 'Install.ps1') -ValidateOnly -Destination $destination
if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $destination)) { throw 'Valid package validation failed or wrote destination.' }
Write-Host 'PASS complete release validates without installing or changing profiles'
Add-Content -LiteralPath (Join-Path $scratch 'dist\AutoCAD.AI.Assistant.bundle\Contents\pdf_worker.py') -Value '# tamper regression test'
$previousErrorAction = $ErrorActionPreference
try {
    $ErrorActionPreference = 'Continue'
    $output = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scratch 'Install.ps1') -ValidateOnly -Destination $destination 2>&1
    $tamperedExit = $LASTEXITCODE
} finally { $ErrorActionPreference = $previousErrorAction }
if ($tamperedExit -eq 0 -or (Test-Path -LiteralPath $destination)) { throw 'Tampered package accepted or wrote destination.' }
Write-Host 'PASS altered release is rejected before destination changes'
