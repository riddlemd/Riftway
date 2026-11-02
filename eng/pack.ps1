# Packs Riftway's NuGet packages (Riftway, Riftway.Cef, Riftway.AutoUpdater, Riftway.Blazor) into artifacts/pkg, a folder an app can use as a local feed.
# Usage: eng/pack.ps1 [-Configuration Release] [-Output <dir>]
param(
    [string]$Configuration = 'Release',
    [string]$Output = (Join-Path $PSScriptRoot '..' 'artifacts' 'pkg')
)
$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot '..'
New-Item -ItemType Directory -Force $Output | Out-Null
foreach ($project in 'src/Riftway.Cef/Riftway.Cef.csproj', 'src/Riftway/Riftway.csproj', 'src/Riftway.AutoUpdater/Riftway.AutoUpdater.csproj', 'src/Riftway.Blazor/Riftway.Blazor.csproj') {
    dotnet pack (Join-Path $root $project) -c $Configuration -o $Output -nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack $project failed ($LASTEXITCODE)" }
}
Get-ChildItem $Output -Filter *.nupkg | Select-Object Name, Length
