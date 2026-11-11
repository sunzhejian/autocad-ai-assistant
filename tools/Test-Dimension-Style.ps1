param([string]$PluginDll)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(-not $PluginDll){$PluginDll=Join-Path $root 'bin\Release\AutoCAD.AI.Assistant.dll'}
$output=Join-Path $root ('artifacts\dimension-style-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$plan=Join-Path $output 'source.plan.json'
$commands=@(@{type='standard'})
foreach($pair in @(@('DIMTXT',35),@('DIMASZ',25),@('DIMTSZ',25),@('DIMEXO',10),@('DIMEXE',15),@('DIMGAP',10))){
    $commands+=@{type='setvar';name=$pair[0];value=$pair[1]}
}
$commands+=@{type='frame';size='A3';scale='1:100'}
$commands+=@{type='dimlinear';p1=@(100,100);p2=@(200,100);position=@(150,50);value=100;confidence='read';layer='SOURCE_STYLE'}
@{sourceUnit='cm';coordinateUnit='source';commands=$commands} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $plan -Encoding UTF8
& (Join-Path $PSScriptRoot 'Reproduce-Cad.ps1') -Plan $plan -Name 'source-style' -PluginDll $PluginDll -OutputDirectory $output
& python (Join-Path $PSScriptRoot 'verify_dimension_sizing.py') (Join-Path $output 'source-style.dxf') --output (Join-Path $output 'checks.json')
if($LASTEXITCODE -ne 0){throw 'Native source dimension sizing failed'}
Write-Host "REPORT=$output\checks.json"
