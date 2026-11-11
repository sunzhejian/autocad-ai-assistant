param([Parameter(Mandatory=$true)][string]$Plan, [Parameter(Mandatory=$true)][string]$Name, [string]$AutoCADPath = 'D:\AutoCAD 2024', [switch]$PlotPdf, [string]$PluginDll, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ($Name -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Name must use ASCII letters, digits, underscores or hyphens' }
$root = Split-Path $PSScriptRoot -Parent
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root 'artifacts\reproductions' }
if (-not $PluginDll) { $PluginDll = Join-Path $root 'bin\Release\AutoCAD.AI.Assistant.dll' }
if (-not (Test-Path -LiteralPath $PluginDll -PathType Leaf)) { throw 'Plugin DLL not found.' }
$PluginDll = [IO.Path]::GetFullPath($PluginDll)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$scratch = Join-Path $env:TEMP ('cadai-repro-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$plugin = Join-Path $scratch 'AutoCAD.AI.Assistant.dll'
Copy-Item -LiteralPath $PluginDll -Destination $plugin
$pluginHash = (Get-FileHash -LiteralPath $plugin).Hash.ToLowerInvariant()
$env:CADAI_PLUGIN_DLL = $plugin
$env:CADAI_DATA_DIR = Join-Path $scratch 'plugin-data'
$env:CADAI_TEST_REPORT = Join-Path $scratch 'report.json'
$env:CADAI_REPRO_PLAN = Join-Path $scratch 'plan.json'
$env:CADAI_REPRO_OUTPUT = Join-Path $scratch $Name
Copy-Item -LiteralPath $Plan -Destination $env:CADAI_REPRO_PLAN
$inputPlanHash = (Get-FileHash -LiteralPath $env:CADAI_REPRO_PLAN).Hash.ToLowerInvariant()
$testDll = Join-Path $scratch 'CadIntegrationTests.dll'
$helperSource = Join-Path $scratch 'CadIntegrationTests.cs'
Copy-Item -LiteralPath (Join-Path $root 'tests\CadIntegrationTests.cs') -Destination $helperSource
$helperHash = (Get-FileHash -LiteralPath $helperSource).Hash.ToLowerInvariant()
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:library /r:System.Web.Extensions.dll "/r:$AutoCADPath\acmgd.dll" "/r:$AutoCADPath\acdbmgd.dll" "/r:$AutoCADPath\accoremgd.dll" "/out:$testDll" $helperSource
if ($LASTEXITCODE -ne 0) { throw 'Integration helper build failed' }
$plotCommands=@()
if($PlotPdf){
    $rootPlan=Get-Content -LiteralPath $Plan -Raw -Encoding UTF8 | ConvertFrom-Json
    $frame=@($rootPlan.commands | Where-Object {$_.type -eq 'frame'}) | Select-Object -First 1
    if(-not $frame){throw 'PDF export needs a top-level frame command'}
    $paper=@{A0=@(1189,841);A1=@(841,594);A2=@(594,420);A3=@(420,297);A4=@(297,210)}
    $size=if($frame.size){([string]$frame.size).ToUpperInvariant()}else{'A3'}
    if(-not $paper.ContainsKey($size)){throw 'Unsupported paper size'}
    $ratio=1.0
    if($frame.scale){
        $scaleText=([string]$frame.scale).Replace([char]0x2236,[char]58).Replace([char]0xFF1A,[char]58)
        $parts=$scaleText.Split(':')
        if($parts.Count -eq 2){$ratio=[double]::Parse($parts[1],[cultureinfo]::InvariantCulture)/[double]::Parse($parts[0],[cultureinfo]::InvariantCulture)}
        else{$ratio=[double]::Parse($scaleText,[cultureinfo]::InvariantCulture)}
    }
    $coordinateFactor=1.0
    if($rootPlan.coordinateUnit -eq 'source'){
        if($rootPlan.sourceUnit -eq 'cm'){$coordinateFactor=10.0}
        elseif($rootPlan.sourceUnit -eq 'm'){$coordinateFactor=1000.0}
    }
    $origin=@(0.0,0.0)
    if($frame.origin){
        if($frame.origin -is [System.Array]){$origin=@($frame.origin[0]*$coordinateFactor,$frame.origin[1]*$coordinateFactor)}
        else{$origin=@($frame.origin.x*$coordinateFactor,$frame.origin.y*$coordinateFactor)}
    }
    $env:CADAI_PLOT_RATIO=$ratio.ToString([cultureinfo]::InvariantCulture)
    $env:CADAI_PLOT_PAPER_WIDTH=([double]$paper[$size][0]).ToString([cultureinfo]::InvariantCulture)
    $env:CADAI_PLOT_PAPER_HEIGHT=([double]$paper[$size][1]).ToString([cultureinfo]::InvariantCulture)
    $env:CADAI_PLOT_ORIGIN_X=([double]$origin[0]).ToString([cultureinfo]::InvariantCulture)
    $env:CADAI_PLOT_ORIGIN_Y=([double]$origin[1]).ToString([cultureinfo]::InvariantCulture)
    $plotDll=Join-Path $scratch 'NativePdfPlot.dll'
    & $compiler /nologo /target:library "/r:$AutoCADPath\acmgd.dll" "/r:$AutoCADPath\acdbmgd.dll" "/r:$AutoCADPath\accoremgd.dll" "/out:$plotDll" (Join-Path $root 'tools\NativePdfPlot.cs')
    if($LASTEXITCODE -ne 0){throw 'PDF helper build failed'}
    $plotCommands=@('_.NETLOAD',('"'+$plotDll.Replace('\','/')+'"'))
}
$script = Join-Path $scratch 'repro.scr'
$lines = @('FILEDIA', '0', '(setq cadai_trusted (getvar "TRUSTEDPATHS"))',
    ('(setvar "TRUSTEDPATHS" (strcat cadai_trusted ";' + $scratch.Replace('\','/') + '"))'),
    '_.NETLOAD', ('"' + $plugin.Replace('\','/') + '"'), '_.NETLOAD', ('"' + $testDll.Replace('\','/') + '"'))+$plotCommands+@(
    '(setvar "TRUSTEDPATHS" cadai_trusted)', 'AIREPRO')
if($PlotPdf){$lines+=@('AIPDF')}
$lines+=@('_.QUIT','_Y','')
$lines | Set-Content -LiteralPath $script -Encoding ASCII
& python (Join-Path $PSScriptRoot 'run_console.py') (Join-Path $AutoCADPath 'accoreconsole.exe') $script (Join-Path $output ($Name + '.log'))
if ($LASTEXITCODE -ne 0) { throw 'Native console did not complete successfully; outputs are not accepted.' }
if (-not (Test-Path -LiteralPath $env:CADAI_TEST_REPORT)) { throw 'No execution report' }
$nativeReport = Get-Content -LiteralPath $env:CADAI_TEST_REPORT -Raw -Encoding UTF8 | ConvertFrom-Json
if (@($nativeReport | Where-Object { $_.error -or $_.passed -eq $false -or $_.result -eq $false }).Count -gt 0) { throw 'Native report rejected this reproduction.' }
$receipts = @($nativeReport | Where-Object { $_.receipt_path })
if ($receipts.Count -ne 1 -or -not (Test-Path -LiteralPath $receipts[0].receipt_path -PathType Leaf)) { throw 'Exactly one native receipt is required.' }
$receipt = Get-Content -LiteralPath $receipts[0].receipt_path -Raw -Encoding UTF8 | ConvertFrom-Json
if ($receipt.native_checks_passed -ne $true -or $receipt.source_accuracy_verified -ne $false) { throw 'Native receipt has an invalid verification scope.' }
if ((Get-FileHash -LiteralPath $Plan).Hash.ToLowerInvariant() -ne $inputPlanHash) { throw 'Input plan changed during native execution.' }
Copy-Item -LiteralPath $env:CADAI_TEST_REPORT -Destination (Join-Path $output ($Name + '.native.json'))
Copy-Item -LiteralPath $receipts[0].receipt_path -Destination (Join-Path $output ($Name + '.receipt.json'))
$extensions=@('.dwg','.dxf');if($PlotPdf){$extensions+=@('.pdf')}
foreach ($extension in $extensions) {
    $file = $env:CADAI_REPRO_OUTPUT + $extension
    if (-not (Test-Path -LiteralPath $file)) { throw "Reproduction failed: missing $extension" }
    Copy-Item -LiteralPath $file -Destination (Join-Path $output ($Name + $extension))
}
$hashes = [ordered]@{}
foreach ($extension in @($extensions) + '.receipt.json') {
    $hashes[$extension] = (Get-FileHash -LiteralPath (Join-Path $output ($Name + $extension))).Hash.ToLowerInvariant()
}
[ordered]@{schema=2;plugin_sha256=$pluginHash;plan_sha256=$inputPlanHash;helper_sha256=$helperHash;receipt_plan_sha256=$receipt.plan_sha256;output_sha256=$hashes;completed=$true} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output ($Name + '.execution.json')) -Encoding UTF8
Write-Host "Saved reproduction: $output\$Name.dwg"
