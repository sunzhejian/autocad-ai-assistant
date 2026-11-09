param([Parameter(Mandatory=$true)][string]$Drawing,[Parameter(Mandatory=$true)][string]$Name,[string]$AutoCADPath='D:\AutoCAD 2024')
$ErrorActionPreference='Stop'
if ($Name -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Name must be ASCII letters, digits, underscores or hyphens.' }
$root=Split-Path $PSScriptRoot -Parent
$scratch=Join-Path $env:TEMP ('cadai-export-'+[guid]::NewGuid().ToString('N'))
$output=Join-Path $root 'artifacts\fidelity'
New-Item -ItemType Directory -Path $scratch,$output -Force | Out-Null
$copy=Join-Path $scratch 'drawing.dwg'
Copy-Item -LiteralPath $Drawing -Destination $copy
$env:CADAI_REPRO_OUTPUT=Join-Path $scratch $Name
$helper=Join-Path $scratch 'NativeDrawingExport.dll'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:library "/r:$AutoCADPath\acmgd.dll" "/r:$AutoCADPath\acdbmgd.dll" "/r:$AutoCADPath\accoremgd.dll" "/out:$helper" (Join-Path $PSScriptRoot 'NativeDrawingExport.cs') (Join-Path $PSScriptRoot 'NativePdfPlot.cs')
if ($LASTEXITCODE -ne 0) { throw 'Native export helper compilation failed.' }
$script=Join-Path $scratch 'export.scr'
@('FILEDIA','0','(setq cadai_trusted (getvar "TRUSTEDPATHS"))',
    ('(setvar "TRUSTEDPATHS" (strcat cadai_trusted ";'+$scratch.Replace('\','/')+'"))'),
    '_.NETLOAD',('"'+$helper.Replace('\','/')+'"'),'(setvar "TRUSTEDPATHS" cadai_trusted)','AIEXPORTCOPY','_.QUIT','_Y','') | Set-Content -LiteralPath $script -Encoding ASCII
# Only the copied drawing and an isolated profile are opened by CoreConsole.
& python (Join-Path $PSScriptRoot 'run_console.py') (Join-Path $AutoCADPath 'accoreconsole.exe') $script (Join-Path $output ($Name+'.export.log')) $copy
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath ($env:CADAI_REPRO_OUTPUT+'.export-ok.txt'))) { throw 'Native export failed. See the export log.' }
foreach ($extension in @('.pdf','.dxf')) { Copy-Item -LiteralPath ($env:CADAI_REPRO_OUTPUT+$extension) -Destination (Join-Path $output ($Name+$extension)) }
Write-Host "EXPORTED=$output\$Name.pdf"
