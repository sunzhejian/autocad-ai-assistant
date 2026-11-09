param([switch]$LiveModel, [string]$DrawingSuiteDirectory = 'artifacts\drawing-suite-auto-20261109')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test.ps1 -BuildPlugin
    if ($LASTEXITCODE -ne 0) { throw 'Regression or plugin build failed' }
    & powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test-Cad.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Native CAD checks failed' }
    & powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test-Large-Drawing.ps1 -Name runtime-1_4_1
    if ($LASTEXITCODE -ne 0) { throw 'Large drawing checks failed' }
    $uiMetadata = Join-Path $root 'artifacts\pdf-native\source.json'
    if (-not (Test-Path -LiteralPath $uiMetadata)) {
        $fixture = Join-Path $root 'artifacts\runtime-ui-fixture'
        New-Item -ItemType Directory -Path $fixture -Force | Out-Null
        & python tools\prepare_pdf_test.py $fixture
        if ($LASTEXITCODE -ne 0) { throw 'Synthetic PDF fixture generation failed' }
        $uiMetadata = Join-Path $fixture 'real.json'
    }
    & powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test-Panel.ps1 -Metadata $uiMetadata
    if ($LASTEXITCODE -ne 0) { throw 'WPF panel checks failed' }
    & python -m unittest discover -s tests -p test_pdf_worker.py -v
    if ($LASTEXITCODE -ne 0) { throw 'PDF worker checks failed' }
    & python tools\audit_public.py --history
    if ($LASTEXITCODE -ne 0) { throw 'Public source audit failed' }
    $previewCases = @('annotations','angular','rings','hatch')
    $missingDrawings = @($previewCases | Where-Object { -not (Test-Path -LiteralPath (Join-Path $DrawingSuiteDirectory ($_+'.dwg'))) })
    if ($missingDrawings.Count -eq 0) {
        foreach ($case in $previewCases) {
            & powershell -NoProfile -ExecutionPolicy Bypass -File tools\Export-Drawing.ps1 -Drawing (Join-Path $DrawingSuiteDirectory ($case+'.dwg')) -Name ('suite-preview-'+$case)
            if ($LASTEXITCODE -ne 0) { throw ('Native preview export failed: '+$case) }
        }
        & python tools\render_drawing_preview.py
        if ($LASTEXITCODE -ne 0) { throw 'Drawing preview failed' }
    } else {
        Write-Host 'SKIP optional native preview: generate a drawing suite or supply -DrawingSuiteDirectory.'
    }
    if ($LiveModel) {
        $oldStreaming = $env:CADAI_BENCH_STREAM
        try {
            $env:CADAI_BENCH_STREAM = '1'
            & python -u tools\test_drawing_suite.py --model auto --output artifacts\runtime-auto-1.4.1 --case angular --case rings
            if ($LASTEXITCODE -ne 0) { throw 'Real auto drawing checks failed' }
        } finally { $env:CADAI_BENCH_STREAM = $oldStreaming }
    }
    Write-Host 'RUNTIME_VERIFICATION=PASS'
} finally { Pop-Location }
