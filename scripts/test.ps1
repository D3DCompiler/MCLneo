$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$env:NUGET_PACKAGES = "D:\MCLneo\.nuget"
$env:DOTNET_CLI_HOME = "D:\MCLneo\.dotnet"
$env:TMP = "D:\MCLneo\.tmp"
$env:TEMP = "D:\MCLneo\.tmp"
New-Item -ItemType Directory -Force $env:TMP, $env:NUGET_PACKAGES, $env:DOTNET_CLI_HOME | Out-Null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
$candidates = @()
if ($cmd) { $candidates += $cmd.Source }
$candidates += "D:\dotnet\dotnet.exe"
$dotnet = $null
foreach ($candidate in $candidates) {
    if (-not (Test-Path $candidate)) { continue }
    $sdks = & $candidate --list-sdks 2>$null
    if ($LASTEXITCODE -eq 0 -and $sdks) { $dotnet = $candidate; break }
}
if (-not $dotnet) {
    throw "未找到带 SDK 的 .NET。"
}

Push-Location $root
try {
    & $dotnet run --project tests\MCLneo.Tests\MCLneo.Tests.csproj
    if ($LASTEXITCODE -ne 0) { throw "测试失败" }
}
finally {
    Pop-Location
}
