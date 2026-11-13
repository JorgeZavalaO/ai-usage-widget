param(
    [switch]$SelfTest,
    [string]$Filter,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'tools\toolchain.ps1')

$dist = Join-Path $root 'dist\test'
$out = Join-Path $dist 'AiUsageWidget.Tests.exe'

if (-not $NoBuild) {
    New-Item -ItemType Directory -Force -Path $dist | Out-Null

    $csc = Find-CSharpCompiler
    $isRoslyn = $csc -match 'Roslyn'
    $refDir = Find-ReferenceAssemblies

    $argumentLines = New-Object System.Collections.Generic.List[string]
    $argumentLines.Add('/nologo')
    $argumentLines.Add('/target:exe')
    $argumentLines.Add('/main:AiUsageWidget.Tests.TestRunner')
    $argumentLines.Add('/optimize+')
    $argumentLines.Add('/warn:4')
    if ($isRoslyn) {
        $argumentLines.Add('/langversion:latest')
        $argumentLines.Add('/debug+')
    }
    $argumentLines.Add('/out:"' + $out + '"')

    foreach ($reference in (Get-CompilerReferenceArguments -ReferenceDirectory $refDir)) {
        $argumentLines.Add($reference)
    }

    $sources = @()
    $sources += Get-CSharpSourceFiles -Directory (Join-Path $root 'src')
    $sources += Get-CSharpSourceFiles -Directory (Join-Path $root 'test')
    foreach ($source in $sources) {
        $argumentLines.Add($source)
    }

    $rsp = Join-Path $dist 'test-build.rsp'
    $exitCode = Invoke-CSharpCompiler -Compiler $csc -ResponseFile $rsp -ArgumentLines $argumentLines
    if ($exitCode -ne 0) { throw "Test compilation failed with exit code $exitCode" }
}

if ($SelfTest) {
    Push-Location $env:TEMP
    try {
        & $out --self-test
        $code = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }

    if ($code -ne 1) { throw "Self-test expected exit code 1 but got $code." }
    Write-Host 'Self-test verified: a failing assertion exits nonzero.' -ForegroundColor Green
    # Deliberately propagate the failing exit code so callers can observe it.
    exit 1
}

$testArguments = @()
if ($Filter) { $testArguments += "--filter=$Filter" }

# Run from an unrelated working directory: fixture discovery and temporary
# directories must not depend on the caller's location.
Push-Location $env:TEMP
try {
    & $out @testArguments
    $code = $LASTEXITCODE
}
finally {
    Pop-Location
}

exit $code
