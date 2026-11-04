param(
    [string]$Destination = (Join-Path $env:APPDATA "Autodesk\ApplicationPlugins\AutoCAD.AI.Assistant.bundle")
)

$source = Join-Path $PSScriptRoot "dist\AutoCAD.AI.Assistant.bundle"
if (-not (Test-Path (Join-Path $source "Contents\AutoCAD.AI.Assistant.dll"))) {
    throw "未找到已编译插件，请先编译 Release|x64。"
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Copy-Item -Path (Join-Path $source "*") -Destination $Destination -Recurse -Force

Write-Host "安装完成：$Destination"
Write-Host "请重启 AutoCAD 2024，然后输入 AIPANEL 打开 AI 面板。"
