param([Parameter(Mandatory=$true)][string]$SourcePdf, [int]$Page = 1, [string]$Kind = 'vector')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $env:TEMP ('cadai-pdf-process-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$executable = Join-Path $scratch 'PdfProcessTests.exe'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'pdf_worker.py') -Destination $scratch
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /r:System.Web.Extensions.dll "/out:$executable" (Join-Path $root 'tests\PdfProcessTests.cs') (Join-Path $root 'Services\PdfSourceService.cs') (Join-Path $root 'Services\DiagnosticsLog.cs')
if ($LASTEXITCODE -ne 0) { throw 'PDF process test compilation failed' }
& $executable $SourcePdf $Page $Kind
if ($LASTEXITCODE -ne 0) { throw 'PDF process test failed' }
