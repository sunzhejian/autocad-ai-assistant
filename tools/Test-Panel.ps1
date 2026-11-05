param([string]$AutoCADPath = 'D:\AutoCAD 2024')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\ui'
$scratch = Join-Path $env:TEMP ('cadai-ui-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch,$output -Force | Out-Null
$env:CADAI_UI_OUTPUT = $output
$env:CADAI_UI_METADATA = Join-Path $root 'artifacts\pdf-native\source.json'
$env:CADAI_PLUGIN_DLL = Join-Path $scratch 'AutoCAD.AI.Assistant.dll'
Copy-Item -LiteralPath (Join-Path $root 'bin\Release\AutoCAD.AI.Assistant.dll') -Destination $env:CADAI_PLUGIN_DLL
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$helper = Join-Path $scratch 'NativePanelSmoke.dll'
& (Join-Path $framework 'csc.exe') /nologo /target:library /r:System.Web.Extensions.dll /r:System.Xaml.dll "/r:$framework\WPF\PresentationFramework.dll" "/r:$framework\WPF\PresentationCore.dll" "/r:$framework\WPF\WindowsBase.dll" "/r:$AutoCADPath\acmgd.dll" "/r:$AutoCADPath\acdbmgd.dll" "/r:$AutoCADPath\accoremgd.dll" "/out:$helper" (Join-Path $PSScriptRoot 'NativePanelSmoke.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI smoke helper build failed' }
$script = Join-Path $scratch 'ui.scr'
@('FILEDIA','0','(setq cadai_trusted (getvar "TRUSTEDPATHS"))',
    ('(setvar "TRUSTEDPATHS" (strcat cadai_trusted ";'+$scratch.Replace('\','/')+'"))'),
    '_.NETLOAD',('"'+$env:CADAI_PLUGIN_DLL.Replace('\','/')+'"'),'_.NETLOAD',('"'+$helper.Replace('\','/')+'"'),
    '(setvar "TRUSTEDPATHS" cadai_trusted)','AIPANELSMOKE','_.QUIT','_Y','') | Set-Content -LiteralPath $script -Encoding ASCII
& python (Join-Path $PSScriptRoot 'run_console.py') (Join-Path $AutoCADPath 'accoreconsole.exe') $script (Join-Path $output 'console.log')
if ($LASTEXITCODE -ne 0) { throw 'UI console failed' }
$result = Get-Content -LiteralPath (Join-Path $output 'result.txt') -Raw
Write-Host $result
if (-not $result.StartsWith('PASS')) { throw 'UI smoke test failed' }
