param(
    [string]$Page = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$env:MCLNEO_HOME = "D:\MCLneo\data"
$env:TMP = "D:\MCLneo\.tmp"
$env:TEMP = "D:\MCLneo\.tmp"
New-Item -ItemType Directory -Force $env:MCLNEO_HOME, $env:TMP | Out-Null
$exe = Join-Path $root "artifacts\publish-final\MCLneo.exe"
if (-not (Test-Path $exe)) {
    $exe = Join-Path $root "artifacts\publish\MCLneo.exe"
}
if (-not (Test-Path $exe)) {
    $exe = Join-Path $root "src\MCLneo.App\bin\Release\net10.0-windows\MCLneo.exe"
}
if (-not (Test-Path $exe)) {
    $exe = Join-Path $root "src\MCLneo.App\bin\Debug\net10.0-windows\MCLneo.exe"
}
if (-not (Test-Path $exe)) {
    throw "还没有构建产物。先运行 scripts\build.ps1。"
}

$arguments = @()
if ($Page) { $arguments = @("--page", $Page) }
Start-Process -FilePath $exe -ArgumentList $arguments
