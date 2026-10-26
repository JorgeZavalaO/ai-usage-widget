param(
    [switch]$Run,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'AIUsageWidget.exe'

if ($Clean -and (Test-Path $dist)) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

function Find-Csc {
    $paths = @(
        'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe',
        'C:\Program Files\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
    )
    foreach ($p in $paths) { if (Test-Path $p) { return $p } }

    $glob = @(
        'C:\Program Files (x86)\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe',
        'C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe'
    )
    foreach ($g in $glob) {
        $hit = Get-ChildItem $g -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    $framework = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (Test-Path $framework) { return $framework }

    throw 'No C# compiler found.'
}

$csc = Find-Csc
$isRoslyn = $csc -match 'Roslyn'

$refDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0'
if (-not (Test-Path $refDir)) { throw "Missing .NET Framework v4.0 reference assemblies: $refDir" }

$assemblies = @(
    'PresentationFramework.dll',
    'PresentationCore.dll',
    'WindowsBase.dll',
    'System.Xaml.dll',
    'System.dll',
    'System.Core.dll',
    'System.Xml.dll',
    'System.Web.Extensions.dll',
    'System.Windows.Forms.dll',
    'System.Drawing.dll',
    'System.Security.dll'
)

$refs = @()
foreach ($a in $assemblies) {
    $full = Join-Path $refDir $a
    if (-not (Test-Path $full)) { throw "Missing reference assembly: $full" }
    $refs += "/r:`"$full`""
}

$sources = @(Get-ChildItem $src -Recurse -Filter *.cs | ForEach-Object { "`"$($_.FullName)`"" })
if ($sources.Count -eq 0) { throw 'No sources found.' }

$argumentLines = New-Object System.Collections.Generic.List[string]
$argumentLines.Add('/nologo')
$argumentLines.Add('/target:winexe')
$argumentLines.Add('/optimize+')
$argumentLines.Add('/debug-')
$argumentLines.Add('/win32manifest:"' + (Join-Path $src 'app.manifest') + '"')
$argumentLines.Add('/out:"' + $out + '"')
if ($isRoslyn) { $argumentLines.Add('/langversion:latest') }
foreach ($r in $refs) { $argumentLines.Add($r) }
foreach ($s in $sources) { $argumentLines.Add($s) }

# Response file avoids every PowerShell 5.1 native-argument quoting pitfall.
$rsp = Join-Path $dist 'build.rsp'
[System.IO.File]::WriteAllLines($rsp, $argumentLines, [System.Text.Encoding]::ASCII)

$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $csc
$startInfo.Arguments = '@"' + $rsp + '"'
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.CreateNoWindow = $true

$process = [System.Diagnostics.Process]::Start($startInfo)
$stdout = $process.StandardOutput.ReadToEnd()
$stderr = $process.StandardError.ReadToEnd()
$process.WaitForExit()

if ($stdout) { Write-Host $stdout.TrimEnd() }
if ($stderr) { Write-Host $stderr.TrimEnd() }
if ($process.ExitCode -ne 0) { throw "Compilation failed with exit code $($process.ExitCode)" }

$size = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host "Built $out ($size KB)" -ForegroundColor Green

if ($Run) {
    Start-Process $out
}
