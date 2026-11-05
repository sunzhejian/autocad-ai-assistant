param([Parameter(Mandatory=$true)][string]$Plan, [Parameter(Mandatory=$true)][string]$Name, [string]$AutoCADPath = 'D:\AutoCAD 2024')
$ErrorActionPreference = 'Stop'
if ($Name -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Name must use ASCII letters, digits, underscores or hyphens' }
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\reproductions'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$scratch = Join-Path $env:TEMP ('cadai-repro-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$plugin = Join-Path $scratch 'AutoCAD.AI.Assistant.dll'
Copy-Item -LiteralPath (Join-Path $root 'bin\Release\AutoCAD.AI.Assistant.dll') -Destination $plugin
$env:CADAI_PLUGIN_DLL = $plugin
$env:CADAI_TEST_REPORT = Join-Path $scratch 'report.json'
$env:CADAI_REPRO_PLAN = Join-Path $scratch 'plan.json'
$env:CADAI_REPRO_OUTPUT = Join-Path $scratch $Name
Copy-Item -LiteralPath $Plan -Destination $env:CADAI_REPRO_PLAN
$testDll = Join-Path $scratch 'CadIntegrationTests.dll'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:library /r:System.Web.Extensions.dll "/r:$AutoCADPath\acmgd.dll" "/r:$AutoCADPath\acdbmgd.dll" "/r:$AutoCADPath\accoremgd.dll" "/out:$testDll" (Join-Path $root 'tests\CadIntegrationTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Integration helper build failed' }
$script = Join-Path $scratch 'repro.scr'
$lines = @('FILEDIA', '0', '(setq cadai_trusted (getvar "TRUSTEDPATHS"))',
    ('(setvar "TRUSTEDPATHS" (strcat cadai_trusted ";' + $scratch.Replace('\','/') + '"))'),
    '_.NETLOAD', ('"' + $plugin.Replace('\','/') + '"'), '_.NETLOAD', ('"' + $testDll.Replace('\','/') + '"'),
    '(setvar "TRUSTEDPATHS" cadai_trusted)', 'AIREPRO', '_.QUIT', '_Y', '')
$lines | Set-Content -LiteralPath $script -Encoding ASCII
& python (Join-Path $PSScriptRoot 'run_console.py') (Join-Path $AutoCADPath 'accoreconsole.exe') $script (Join-Path $output ($Name + '.log'))
if (-not (Test-Path -LiteralPath $env:CADAI_TEST_REPORT)) { throw 'No execution report' }
Copy-Item -LiteralPath $env:CADAI_TEST_REPORT -Destination (Join-Path $output ($Name + '.native.json'))
foreach ($extension in @('.dwg','.dxf')) {
    $file = $env:CADAI_REPRO_OUTPUT + $extension
    if (-not (Test-Path -LiteralPath $file)) { throw "Reproduction failed: missing $extension" }
    Copy-Item -LiteralPath $file -Destination (Join-Path $output ($Name + $extension))
}
Write-Host "Saved reproduction: $output\$Name.dwg"
