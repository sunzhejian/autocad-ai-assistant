param([string]$AutoCADPath = 'D:\AutoCAD 2024')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\native'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$scratch = Join-Path $env:TEMP ('cadai-native-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$plugin = Join-Path $scratch 'AutoCAD.AI.Assistant.dll'
Copy-Item -LiteralPath (Join-Path $root 'bin\Release\AutoCAD.AI.Assistant.dll') -Destination $plugin
$env:CADAI_PLUGIN_DLL = $plugin
$env:CADAI_TEST_REPORT = Join-Path $scratch 'report.json'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testDll = Join-Path $scratch 'CadIntegrationTests.dll'
& $compiler /nologo /target:library /r:System.Web.Extensions.dll "/r:$AutoCADPath\acmgd.dll" "/r:$AutoCADPath\acdbmgd.dll" "/r:$AutoCADPath\accoremgd.dll" "/out:$testDll" (Join-Path $root 'tests\CadIntegrationTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Integration test build failed' }
# AutoCAD scripts accept forward slashes in quoted paths. Only new scratch documents are used.
$plan = Join-Path $scratch 'undo-plan.json'
'{"commands":[{"type":"rect","p1":[0,0],"p2":[120,80]},{"type":"circle","center":[60,40],"radius":20}]}' | Set-Content -LiteralPath $plan -Encoding UTF8
$script = Join-Path $scratch 'verify.scr'
$trust = $scratch.Replace('\','/')
$lines = @('FILEDIA', '0', 'CMDECHO', '1', '(setq cadai_trusted (getvar "TRUSTEDPATHS"))',
    ('(setvar "TRUSTEDPATHS" (strcat cadai_trusted ";' + $trust + '"))'), '_.NETLOAD', ('"' + $plugin.Replace('\','/') + '"'),
    '_.NETLOAD', ('"' + $testDll.Replace('\','/') + '"'), '(setvar "TRUSTEDPATHS" cadai_trusted)', 'AIPLAN', ('"' + $plan.Replace('\','/') + '"'),
    '_.U', 'AIEMPTY', 'AIVERIFY', '_.QUIT', '_Y', '')
$lines | Set-Content -LiteralPath $script -Encoding ASCII
$log = Join-Path $output 'console.log'
& python (Join-Path $PSScriptRoot 'run_console.py') (Join-Path $AutoCADPath 'accoreconsole.exe') $script $log
if (-not (Test-Path -LiteralPath $env:CADAI_TEST_REPORT)) { throw "AutoCAD did not produce a report; see $log" }
Copy-Item -LiteralPath $env:CADAI_TEST_REPORT -Destination (Join-Path $output 'report.json')
$report = Get-Content -LiteralPath $env:CADAI_TEST_REPORT -Raw -Encoding UTF8 | ConvertFrom-Json
$checks = @($report | Where-Object { $null -ne $_.passed })
$checks | Select-Object test,passed | Format-Table
if ($checks.Count -lt 6 -or @($checks | Where-Object { -not $_.passed }).Count -gt 0) { throw "Native integration failed; see $log" }
$report | Where-Object { $null -ne $_.milliseconds } | Format-Table
