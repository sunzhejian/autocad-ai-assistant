param([switch]$BuildPlugin)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    New-Item -ItemType Directory -Path artifacts -Force | Out-Null
    $sources = @('tests\RegressionTests.cs', 'Models\ChatItem.cs', 'Models\ModelConfiguration.cs',
        'Services\CadPlanText.cs', 'Services\CadPlanQuality.cs', 'Services\CadPlanValidator.cs', 'Services\CadPlanNormalizer.cs', 'Services\HatchMaterialPolicy.cs',
        'Services\ImagePreprocessor.cs', 'Services\OpenAiCompatibleClient.cs', 'Services\SecretProtector.cs', 'Services\DiagnosticsLog.cs', 'Services\DrawingPlanCache.cs')
    & (Join-Path $framework 'csc.exe') /nologo /r:System.Web.Extensions.dll /r:System.Net.Http.dll /r:System.Drawing.dll /r:System.Security.dll /out:artifacts\RegressionTests.exe $sources
    if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed' }
    & .\artifacts\RegressionTests.exe
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed' }
    if ($BuildPlugin) {
        & (Join-Path $framework 'MSBuild.exe') AutoCAD.AI.Assistant.csproj /p:Configuration=Release /p:Platform=x64 /v:minimal /nologo
        if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed' }
    }
} finally { Pop-Location }
