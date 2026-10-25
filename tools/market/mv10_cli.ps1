param(
    [Parameter(Mandatory=$true)][string]$DatabaseCopy,
    [string]$OutDir = 'artifacts/mv10'
)
$ErrorActionPreference = 'Stop'
# Run from the repository root. The source database must first be copied by mv10.py --prepare-db.
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
dotnet build src/FcHelper.Cli -c Release
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }
$fchDll = 'src/FcHelper.Cli/bin/Release/net10.0/fch.dll'
function Audit([string]$FileName, [string[]]$Options) {
    dotnet $fchDll market-validate --db $DatabaseCopy @Options --out (Join-Path $OutDir $FileName)
    if ($LASTEXITCODE -ne 0) { throw "Audit failed: $FileName" }
}
# Baseline is the MV-09 hinge at 118; five-fold errors are independent of TraitCritical.
Audit 'app-baseline.json' @('--cuts','110,114,115,116,119,120','--hinge','118')
Audit 'app-attack.json' @('--positions','ST,CF,W','--cuts','110,115,120','--hinge','118','--shapes','yes','--draws','60')
Audit 'app-other.json' @('--positions','CAM,CM,CDM,FB,CB,GK','--grades','11','--cuts','110,115,120','--hinge','118','--shapes','yes','--draws','60')
Audit 'app-final.json' @('--cuts','115')
Audit 'app-calibration.json' @('--grades','11','--cuts','110,115,120','--draws','60')
foreach ($grade in @(8,10,11)) {
    foreach ($pos in @('ST','CF','W')) {
        $trainOvr = @{8=130;10=134;11=136}[$grade]
        dotnet $fchDll factors --db $DatabaseCopy --pos $pos --grade $grade --minovr $trainOvr --offline yes > (Join-Path $OutDir "factors-$pos-$grade.txt")
        if ($LASTEXITCODE -ne 0) { throw 'Factors failed' }
        dotnet $fchDll value --db $DatabaseCopy --pos $pos --grade $grade --top 3000 --offline yes > (Join-Path $OutDir "value-$pos-$grade.txt")
        if ($LASTEXITCODE -ne 0) { throw 'Value failed' }
    }
}
dotnet $fchDll factors --db $DatabaseCopy --pos ST --grade 11 --minovr 130 --offline yes > (Join-Path $OutDir 'factors-ST-11-130.txt')
if ($LASTEXITCODE -ne 0) { throw 'Explicit +11 OVR 130 factors failed' }
