param(
    [string] $AcadPath = "D:\Program Files\CAD\AutoCAD 2022",
    [string] $Configuration = "Release",
    [string] $OutputName = "CodexAutoCADBridge.dll"
)

$ErrorActionPreference = "Stop"

$sources = Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.cs" | Select-Object -ExpandProperty FullName
$outDir = Join-Path $PSScriptRoot "bin\$Configuration"
$dll = Join-Path $outDir $OutputName
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $csc)) {
    $csc = "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
}

if (-not (Test-Path -LiteralPath $csc)) {
    throw "Cannot find .NET Framework csc.exe."
}

$refs = @(
    (Join-Path $AcadPath "AcCoreMgd.dll"),
    (Join-Path $AcadPath "AcDbMgd.dll"),
    (Join-Path $AcadPath "AcMgd.dll")
)

foreach ($ref in $refs) {
    if (-not (Test-Path -LiteralPath $ref)) {
        throw "Missing AutoCAD reference: $ref"
    }
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

& $csc `
    /nologo `
    /target:library `
    /platform:x64 `
    /optimize+ `
    /debug:pdbonly `
    "/out:$dll" `
    "/reference:$($refs[0])" `
    "/reference:$($refs[1])" `
    "/reference:$($refs[2])" `
    "/reference:System.Windows.Forms.dll" `
    "/reference:System.Drawing.dll" `
    $sources

if ($LASTEXITCODE -ne 0) {
    throw "csc.exe failed with exit code $LASTEXITCODE"
}

Write-Host "Built $dll"
