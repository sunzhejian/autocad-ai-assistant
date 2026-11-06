param([string]$PluginPath, [string]$Name = 'current', [string]$AutoCADPath = 'D:\AutoCAD 2024', [switch]$Baseline)
$ErrorActionPreference = 'Stop'
if ($Name -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Name must use ASCII letters, digits, underscores or hyphens' }
$root = Split-Path $PSScriptRoot -Parent
if (-not $PluginPath) { $PluginPath = Join-Path $root 'bin\Release\AutoCAD.AI.Assistant.dll' }
$scratch = Join-Path $env:TEMP ('cadai-large-' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $root 'artifacts\large-drawing'
New-Item -ItemType Directory -Path $scratch,$output -Force | Out-Null
$plugin = Join-Path $scratch 'AutoCAD.AI.Assistant.dll'
Copy-Item -LiteralPath $PluginPath -Destination $plugin
$env:CADAI_PLUGIN_DLL = $plugin; $env:CADAI_DATA_DIR = Join-Path $scratch 'plugin-data'
$env:CADAI_LARGE_REPORT = Join-Path $scratch 'report.json'
$helper = Join-Path $scratch 'LargeDrawingTests.dll'
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:library /r:System.Web.Extensions.dll "/r:$AutoCADPath\acmgd.dll" "/r:$AutoCADPath\acdbmgd.dll" "/r:$AutoCADPath\accoremgd.dll" "/out:$helper" (Join-Path $root 'tests\LargeDrawingTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Large drawing helper build failed' }
$script = Join-Path $scratch 'large.scr'
@('FILEDIA','0','(setq cadai_trusted (getvar "TRUSTEDPATHS"))',
    ('(setvar "TRUSTEDPATHS" (strcat cadai_trusted ";'+$scratch.Replace('\','/')+'"))'),
    '_.NETLOAD',('"'+$plugin.Replace('\','/')+'"'),'_.NETLOAD',('"'+$helper.Replace('\','/')+'"'),
    '(setvar "TRUSTEDPATHS" cadai_trusted)','AILARGEVERIFY','_.QUIT','_Y','') | Set-Content -LiteralPath $script -Encoding ASCII
& python (Join-Path $PSScriptRoot 'run_console.py') (Join-Path $AutoCADPath 'accoreconsole.exe') $script (Join-Path $output ($Name+'.log'))
if ($LASTEXITCODE -ne 0) { throw 'Large drawing console failed' }
Copy-Item -LiteralPath $env:CADAI_LARGE_REPORT -Destination (Join-Path $output ($Name+'.json'))
$report = Get-Content -LiteralPath $env:CADAI_LARGE_REPORT -Raw -Encoding UTF8 | ConvertFrom-Json
if (@($report | Where-Object {$_.error}).Count -gt 0) { throw 'Large drawing probe failed; inspect its report' }
$checks = @($report | Where-Object {$null -ne $_.passed})
if (-not $Baseline -and ($checks.Count -ne 5 -or @($checks | Where-Object {-not $_.passed}).Count -gt 0)) { throw 'Large drawing checks failed' }
$report | ConvertTo-Json -Depth 4
