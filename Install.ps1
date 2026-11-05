param(
    [string]$Destination = (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\AutoCAD.AI.Assistant.bundle")
)

$source = Join-Path $PSScriptRoot "dist\AutoCAD.AI.Assistant.bundle"
$moduleName = "AutoCAD.AI.Assistant.v2.dll"
if (-not (Test-Path (Join-Path $source "Contents\$moduleName"))) {
    throw "Build output not found. Please build Release|x64 first."
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $Destination -Recurse -Force

$srcHash = (Get-FileHash (Join-Path $source "Contents\$moduleName")).Hash
$dstHash = (Get-FileHash (Join-Path $Destination "Contents\$moduleName")).Hash

# Register the bundle folder as an AutoCAD trusted path so the unsigned DLL
# loads without the "unsigned executable" warning on every new build.
$contents = Join-Path $Destination "Contents"
$baseKey = "HKCU:\Software\Autodesk\AutoCAD"
Get-ChildItem $baseKey -ErrorAction SilentlyContinue | ForEach-Object {
    $productRoot = $_.PSPath
    Get-ChildItem $productRoot -ErrorAction SilentlyContinue | ForEach-Object {
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
}

Write-Host "Installed to: $Destination"
Write-Host "DLL_MATCH=$($srcHash -eq $dstHash)"
Write-Host "Trusted path registered for AutoCAD profiles."
Write-Host "Restart AutoCAD 2024 and run command AI."