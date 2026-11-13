param(
    [switch]$Run,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $root 'tools\toolchain.ps1')

$src = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'AIUsageWidget.exe'

if ($Clean -and (Test-Path $dist)) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$csc = Find-CSharpCompiler
$isRoslyn = $csc -match 'Roslyn'
$refDir = Find-ReferenceAssemblies
$refVersion = Split-Path $refDir -Leaf
if ($refVersion -notmatch '^v4\.[89]') {
    Write-Warning "Using $refVersion reference assemblies with the installed .NET Framework 4.8 runtime. Install the .NET Framework 4.8 Developer Pack for a fully matching build."
}

if (-not (Test-NetFramework48Runtime)) {
    throw '.NET Framework 4.8 runtime was not detected. Install the .NET Framework 4.8 runtime and Developer Pack, then retry.'
}

$argumentLines = New-Object System.Collections.Generic.List[string]
$argumentLines.Add('/nologo')
$argumentLines.Add('/target:winexe')
$argumentLines.Add('/optimize+')
$argumentLines.Add('/debug-')
$argumentLines.Add('/warn:4')
$argumentLines.Add('/win32manifest:"' + (Join-Path $src 'app.manifest') + '"')

$icon = Join-Path $root 'assets\icon.ico'
if (Test-Path $icon) {
    $argumentLines.Add('/win32icon:"' + $icon + '"')
}
else {
    Write-Warning 'assets\icon.ico is missing; the exe will ship without an application icon.'
}

$argumentLines.Add('/out:"' + $out + '"')
if ($isRoslyn) {
    $argumentLines.Add('/langversion:latest')
    $argumentLines.Add('/deterministic+')
}

foreach ($reference in (Get-CompilerReferenceArguments -ReferenceDirectory $refDir)) {
    $argumentLines.Add($reference)
}
foreach ($source in (Get-CSharpSourceFiles -Directory $src)) {
    $argumentLines.Add($source)
}

# A UTF-8 response file preserves non-ASCII checkout paths. The previous ASCII
# encoding silently replaced characters the compiler needed.
$rsp = Join-Path $dist 'build.rsp'
$exitCode = Invoke-CSharpCompiler -Compiler $csc -ResponseFile $rsp -ArgumentLines $argumentLines

if ($exitCode -ne 0) { throw "Compilation failed with exit code $exitCode" }

$size = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host "Built $out ($size KB)" -ForegroundColor Green

if ($Run) {
    Start-Process $out
}
