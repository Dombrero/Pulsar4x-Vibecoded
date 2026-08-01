$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$solutionRoot = Join-Path $repoRoot "Pulsar4X"
$dotnet = "dotnet"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $fallbackDotnet = "C:\Program Files\dotnet\dotnet.exe"
    if (Test-Path $fallbackDotnet) {
        $dotnet = $fallbackDotnet
    } else {
        throw "dotnet wurde nicht gefunden. Installiere bitte das .NET 8 SDK."
    }
}

Set-Location $solutionRoot

& $dotnet restore .\Pulsar4X.sln
& $dotnet build .\Pulsar4X.sln -c Debug
& $dotnet run --project .\Pulsar4X.Client.Host\Pulsar4X.Client.Host.csproj
