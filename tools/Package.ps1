param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'releases' }
$source = Join-Path $root 'dist\AutoCAD.AI.Assistant.bundle'
[xml]$manifest = Get-Content -LiteralPath (Join-Path $source 'PackageContents.xml') -Raw -Encoding UTF8
$version = [string]$manifest.ApplicationPackage.AppVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
$dll = Join-Path $source 'Contents\AutoCAD.AI.Assistant.v2.dll'
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).FileVersion -ne ($version + '.0')) { throw 'DLL and manifest versions differ.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stage = Join-Path $OutputDirectory ('stage-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $stage 'dist\AutoCAD.AI.Assistant.bundle\Contents') -Force | Out-Null
$paths = @('Install.ps1','Install.cmd','INSTALL.md','dist/AutoCAD.AI.Assistant.bundle/PackageContents.xml','dist/AutoCAD.AI.Assistant.bundle/Contents/AutoCAD.AI.Assistant.v2.dll','dist/AutoCAD.AI.Assistant.bundle/Contents/pdf_worker.py')
foreach ($path in $paths) {
    $origin = if ($path -eq 'INSTALL.md') { Join-Path $root 'docs\INSTALL.md' } else { Join-Path $root $path }
    Copy-Item -LiteralPath $origin -Destination (Join-Path $stage $path)
}
$files = @($paths | ForEach-Object { [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $stage $_)).Hash.ToLowerInvariant()} })
[ordered]@{schema=1;version=$version;repository='https://github.com/sunzhejian/autocad-ai-assistant';files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'release-manifest.json') -Encoding UTF8
$zip = Join-Path $OutputDirectory ('AutoCAD-AI-Assistant-v' + $version + '-win64.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $entries = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') -and -not $_.FullName.EndsWith('\') })
    $expected = @($paths) + 'release-manifest.json'
    if ($entries.Count -ne $expected.Count) { throw 'Unexpected release archive entry count.' }
    foreach ($entry in $entries) {
        $name = $entry.FullName.Replace('\','/')
        if ($name -notin $expected) { throw "Unexpected archive content: $name" }
        $stream = $entry.Open()
        try {
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','').ToLowerInvariant() } finally { $sha.Dispose() }
        } finally { $stream.Dispose() }
        if ($hash -ne (Get-FileHash -LiteralPath (Join-Path $stage $name)).Hash.ToLowerInvariant()) { throw "Archive content differs: $name" }
    }
} finally { $archive.Dispose() }
$digest = (Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()
($digest + '  ' + [IO.Path]::GetFileName($zip)) | Set-Content -LiteralPath ($zip + '.sha256') -Encoding ASCII
Write-Host "PACKAGE=$zip"
Write-Host "SHA256=$digest"
Write-Host 'ARCHIVE_FILES=7 VERIFIED=True'
