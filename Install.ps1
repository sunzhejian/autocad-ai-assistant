param(
    [string]$Destination = (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\AutoCAD.AI.Assistant.bundle"),
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

# Release archives carry a strict manifest. Check every packaged file before
# changing the installed bundle; source builds continue to work without it.
$releaseManifest = Join-Path $PSScriptRoot 'release-manifest.json'
if (Test-Path -LiteralPath $releaseManifest) {
    $release = Get-Content -LiteralPath $releaseManifest -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($release.schema -ne 1 -or @($release.files).Count -ne 6) { throw 'Invalid release manifest.' }
    $packageRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\') + '\'
    $expectedPaths = @('Install.ps1','Install.cmd','INSTALL.md','dist/AutoCAD.AI.Assistant.bundle/PackageContents.xml','dist/AutoCAD.AI.Assistant.bundle/Contents/AutoCAD.AI.Assistant.v2.dll','dist/AutoCAD.AI.Assistant.bundle/Contents/pdf_worker.py')
    $seen = @{}
    foreach ($entry in $release.files) {
        if ($entry.path -notin $expectedPaths -or $seen.ContainsKey([string]$entry.path)) { throw 'Unexpected release file.' }
        $seen[[string]$entry.path] = $true
        $item = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $entry.path))
        if (-not $item.StartsWith($packageRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid release path.' }
        if ((Get-FileHash -LiteralPath $item).Hash -ne $entry.sha256) { throw "Package file failed integrity check: $($entry.path)" }
    }
}

$source = Join-Path $PSScriptRoot "dist\AutoCAD.AI.Assistant.bundle"
$manifest = New-Object System.Xml.XmlDocument
$manifest.Load((Join-Path $source 'PackageContents.xml'))
$moduleName = [IO.Path]::GetFileName([string]$manifest.ApplicationPackage.Components.ComponentEntry.ModuleName)
if ([string]::IsNullOrWhiteSpace($moduleName)) { throw 'Invalid bundle module name.' }
if (-not (Test-Path (Join-Path $source "Contents\$moduleName"))) {
    throw "Build output not found. Please build Release|x64 first."
}
if (-not (Test-Path -LiteralPath (Join-Path $source 'Contents\pdf_worker.py'))) {
    throw 'PDF worker not found. Rebuild the complete Release bundle first.'
}

$srcHash = (Get-FileHash (Join-Path $source "Contents\$moduleName")).Hash
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $source "Contents\$moduleName")).FileVersion
if ($version -ne ([string]$manifest.ApplicationPackage.AppVersion + '.0')) { throw 'Manifest and DLL versions differ.' }
if ($ValidateOnly) { Write-Host "BUNDLE_VALID=True VERSION=$version"; return }
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$installedModule = 'AutoCAD.AI.Assistant.' + $version + '-' + $srcHash.Substring(0,12).ToLowerInvariant() + '.dll'
$contents = Join-Path $Destination 'Contents'
New-Item -ItemType Directory -Path $contents -Force | Out-Null
$installedDll = Join-Path $contents $installedModule
if (-not (Test-Path -LiteralPath $installedDll) -or (Get-FileHash -LiteralPath $installedDll).Hash -ne $srcHash) {
    Copy-Item -LiteralPath (Join-Path $source "Contents\$moduleName") -Destination $installedDll -Force
}
Copy-Item -LiteralPath (Join-Path $source 'Contents\pdf_worker.py') -Destination (Join-Path $contents 'pdf_worker.py') -Force
$dstHash = (Get-FileHash -LiteralPath $installedDll).Hash
$pdfHashMatches = (Get-FileHash (Join-Path $source 'Contents\pdf_worker.py')).Hash -eq (Get-FileHash (Join-Path $Destination 'Contents\pdf_worker.py')).Hash
if ($srcHash -ne $dstHash -or -not $pdfHashMatches) { throw 'Installed bundle integrity check failed.' }
# Publish the manifest last. Loaded DLLs remain untouched until CAD is restarted.
$manifest.ApplicationPackage.Components.ComponentEntry.ModuleName = './Contents/' + $installedModule
$installedManifest = Join-Path $Destination 'PackageContents.xml'
$temporaryManifest = $installedManifest + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
try {
    $manifest.Save($temporaryManifest)
    if (Test-Path -LiteralPath $installedManifest) { [IO.File]::Replace($temporaryManifest, $installedManifest, ($installedManifest + '.bak')) }
    else { [IO.File]::Move($temporaryManifest, $installedManifest) }
} finally {
    if (Test-Path -LiteralPath $temporaryManifest) { Remove-Item -LiteralPath $temporaryManifest -Force }
}

# Register the bundle folder as an AutoCAD trusted path so the unsigned DLL
# loads without the "unsigned executable" warning on every new build.
$contents = Join-Path $Destination "Contents"
$baseKey = "HKCU:\Software\Autodesk\AutoCAD\R24.3"
Get-ChildItem $baseKey -ErrorAction SilentlyContinue | ForEach-Object {
        $profiles = Join-Path $_.PSPath "Profiles"
        if (-not (Test-Path $profiles)) { return }
        Get-ChildItem $profiles -ErrorAction SilentlyContinue | ForEach-Object {
            $vars = Join-Path $_.PSPath "Variables"
            if (-not (Test-Path $vars)) { return }
            $current = (Get-ItemProperty -Path $vars -Name TRUSTEDPATHS -ErrorAction SilentlyContinue).TRUSTEDPATHS
            if ($null -eq $current) { $current = "" }
            if ($current -like "*$contents*") { return }
            $updated = if ($current.Trim().Length -eq 0) { $contents } else { "$current;$contents" }
            Set-ItemProperty -Path $vars -Name TRUSTEDPATHS -Value $updated
        }
}

Write-Host "Installed to: $Destination"
Write-Host "MODULE=$installedModule"
Write-Host "DLL_MATCH=$($srcHash -eq $dstHash)"
Write-Host "PDF_WORKER_MATCH=$pdfHashMatches"
Write-Host "Trusted path registered for AutoCAD profiles."
Write-Host "Restart AutoCAD 2024 and run command AI."
