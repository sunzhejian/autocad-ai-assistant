param([string]$SourcePdf, [int]$Page = 3, [string]$AutoCADPath = 'D:\AutoCAD 2024', [switch]$PlotPdf)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $env:TEMP ('cadai-pdf-' + [guid]::NewGuid().ToString('N'))
$output = Join-Path $root 'artifacts\pdf-native'
New-Item -ItemType Directory -Path $scratch,$output -Force | Out-Null
$prepare = @( (Join-Path $PSScriptRoot 'prepare_pdf_test.py'), $scratch )
if ($SourcePdf) { $prepare += @('--source', $SourcePdf, '--page', [string]$Page) }
& python @prepare
if ($LASTEXITCODE -ne 0) { throw 'PDF preparation failed' }
$plugin = Join-Path $scratch 'AutoCAD.AI.Assistant.dll'
Copy-Item -LiteralPath (Join-Path $root 'bin\Release\AutoCAD.AI.Assistant.dll') -Destination $plugin
$env:CADAI_PLUGIN_DLL = $plugin
$env:CADAI_DATA_DIR = Join-Path $scratch 'plugin-data'
$env:CADAI_SMALL_PDF = Join-Path $scratch 'small.json'
$env:CADAI_REAL_PDF = Join-Path $scratch 'real.json'
$env:CADAI_MIXED_PDF = Join-Path $scratch 'mixed.json'
$env:CADAI_PDF_REPORT = Join-Path $scratch 'report.json'
$env:CADAI_PDF_OUTPUT = Join-Path $scratch 'imported'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$helper = Join-Path $scratch 'PdfIntegrationTests.dll'
& $compiler /nologo /target:library /r:System.Web.Extensions.dll "/r:$AutoCADPath\acmgd.dll" "/r:$AutoCADPath\acdbmgd.dll" "/r:$AutoCADPath\accoremgd.dll" "/out:$helper" (Join-Path $root 'tests\PdfIntegrationTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'PDF native test build failed' }
$script = Join-Path $scratch 'pdf.scr'
$plotCommands = @()
if ($PlotPdf) {
    $metadata = Get-Content -LiteralPath $env:CADAI_REAL_PDF -Raw -Encoding UTF8 | ConvertFrom-Json
    $env:CADAI_REPRO_OUTPUT = $env:CADAI_PDF_OUTPUT
    $env:CADAI_PLOT_RATIO = '1'
    $env:CADAI_PLOT_PAPER_WIDTH = ([double]$metadata.width_mm).ToString([cultureinfo]::InvariantCulture)
    $env:CADAI_PLOT_PAPER_HEIGHT = ([double]$metadata.height_mm).ToString([cultureinfo]::InvariantCulture)
    $env:CADAI_PLOT_ORIGIN_X = '0'; $env:CADAI_PLOT_ORIGIN_Y = '0'
    $plotter = Join-Path $scratch 'NativePdfPlot.dll'
    & $compiler /nologo /target:library "/r:$AutoCADPath\acmgd.dll" "/r:$AutoCADPath\acdbmgd.dll" "/r:$AutoCADPath\accoremgd.dll" "/out:$plotter" (Join-Path $PSScriptRoot 'NativePdfPlot.cs')
    if ($LASTEXITCODE -ne 0) { throw 'PDF plot helper build failed' }
    $plotCommands = @('_.NETLOAD', ('"' + $plotter.Replace('\','/') + '"'), 'AIPDF')
}
$lines = @('FILEDIA','0','(setq cadai_trusted (getvar "TRUSTEDPATHS"))',
    ('(setvar "TRUSTEDPATHS" (strcat cadai_trusted ";'+$scratch.Replace('\','/')+'"))'),
    '_.NETLOAD',('"'+$plugin.Replace('\','/')+'"'),'_.NETLOAD',('"'+$helper.Replace('\','/')+'"'),
    'AIPDFTEST','_.U','AIPDFUNDO','AIPDFMIXED','_.U','AIPDFUNDO','AIPDFREAL') + $plotCommands + @('(setvar "TRUSTEDPATHS" cadai_trusted)','_.QUIT','_Y','')
$lines | Set-Content -LiteralPath $script -Encoding ASCII
& python (Join-Path $PSScriptRoot 'run_console.py') (Join-Path $AutoCADPath 'accoreconsole.exe') $script (Join-Path $output 'console.log')
if ($LASTEXITCODE -ne 0) { throw 'PDF console failed' }
if (-not (Test-Path -LiteralPath $env:CADAI_PDF_REPORT)) { throw 'No native PDF report' }
Copy-Item -LiteralPath $env:CADAI_PDF_REPORT -Destination (Join-Path $output 'report.json')
$report = Get-Content -LiteralPath $env:CADAI_PDF_REPORT -Raw -Encoding UTF8 | ConvertFrom-Json
$checks = @($report | Where-Object { $null -ne $_.passed })
$checks | Format-Table test,passed
if ($checks.Count -ne 9 -or @($checks | Where-Object { -not $_.passed }).Count -gt 0 -or @($report | Where-Object { $_.error }).Count -gt 0) { throw 'Native PDF test failed; see artifacts/pdf-native/report.json' }
$extensions = @('.dwg','.dxf'); if ($PlotPdf) { $extensions += '.pdf' }
foreach ($extension in $extensions) { Copy-Item -LiteralPath ($env:CADAI_PDF_OUTPUT+$extension) -Destination (Join-Path $output ('contest-import'+$extension)) }
Copy-Item -LiteralPath $env:CADAI_REAL_PDF -Destination (Join-Path $output 'source.json')
