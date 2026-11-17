#Requires -Version 5.1
<#
  kpcap build script
  用法：  .\build.ps1            构建 Release
          .\build.ps1 -Clean     先清 bin/obj 再构建
          .\build.ps1 -Run "https://example.com/"   构建后直接跑一次
#>
param(
    [switch]$Clean,
    [string]$Run
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'src\kpcap.csproj'
$exe  = Join-Path $root 'src\bin\Release\netcoreapp3.1\kpcap.exe'

Push-Location $root
try {
    if ($Clean) {
        foreach ($d in @('src\bin', 'src\obj')) {
            $p = Join-Path $root $d
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force }
        }
    }

    & dotnet restore $proj
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed (exit $LASTEXITCODE)" }

    & dotnet build $proj -c Release
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed (exit $LASTEXITCODE)" }

    if (-not (Test-Path -LiteralPath $exe)) { throw "build output not found: $exe" }
    $size = (Get-Item -LiteralPath $exe).Length
    Write-Host "OK  $exe  ($size B)"

    if ($Run) {
        Write-Host "run: $exe -u $Run"
        & $exe -u $Run
        Write-Host "exit=$LASTEXITCODE"
    }
}
finally {
    Pop-Location
}
