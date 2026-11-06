param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$Publish,
    [switch]$SelfContained
)

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
    throw "未找到带 SDK 的 .NET。请安装 .NET 10 SDK，或把 dotnet 加入 PATH。"
}

Push-Location $root
try {
    & $dotnet restore MCLneo.slnx
    if ($LASTEXITCODE -ne 0) { throw "restore 失败" }

    & $dotnet build MCLneo.slnx -c $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) { throw "build 失败" }

    if ($Publish) {
        $out = Join-Path $root "artifacts\publish-final"
        $publishArgs = @(
            "publish", "src\MCLneo.App\MCLneo.App.csproj",
            "-c", $Configuration,
            "-r", "win-x64",
            "-o", $out
        )
        if ($SelfContained) {
            $publishArgs += @("--self-contained", "true")
        } else {
            $publishArgs += @("--self-contained", "false")
        }

        & $dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "publish 失败" }
        Write-Host "发布目录：$out"
    }

    Write-Host "构建完成（$Configuration）"
}
finally {
    Pop-Location
}
