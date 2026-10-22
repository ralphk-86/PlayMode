<#
.SYNOPSIS
  Builds the files that ship: the tray app, playmodectl and the bundled service helper, in one folder.
  The release workflow and the installer both start from this folder.
.EXAMPLE
  ./build/publish.ps1 -Version 0.2.0 -Output artifacts/publish
#>
param(
    [string]$Version = '0.0.0-dev',
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\publish')
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
foreach ($project in 'src\PlayMode.App\PlayMode.App.csproj', 'src\PlayMode.Cli\PlayMode.Cli.csproj') {
    dotnet publish (Join-Path $root $project) -c Release -o $Output -p:Version=$Version -p:DebugType=none
    if ($LASTEXITCODE) { throw "dotnet publish $project failed" }
}

# Referencing the helper project (so it builds first) also drops its launcher next to the app. It belongs only in
# helper\, which is what setup copies to Program Files.
Get-ChildItem $Output -File -Filter 'PlayMode.Helper.*' | Remove-Item

$required = 'PlayMode.exe', 'playmodectl.exe', 'helper\PlayMode.Helper.exe', 'helper\PlayMode.Helper.dll'
foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $Output $file))) { throw "Missing from publish output: $file" }
}
Write-Host "Published PlayMode $Version to $Output"
