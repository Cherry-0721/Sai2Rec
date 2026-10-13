[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$buildDir = Join-Path $root 'build'
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'

if (-not (Test-Path -LiteralPath $csc)) {
    throw "找不到 .NET Framework C# 编译器：$csc"
}

New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
$sources = @(
    'CanvasCore.cs',
    'Meta.cs',
    'Store.cs',
    'Recording.cs',
    'Sampler.cs',
    'Exporter.cs',
    'gui.cs'
) | ForEach-Object { Join-Path (Join-Path $root 'src') $_ }

& $csc /nologo /platform:anycpu /target:winexe /r:System.Drawing.dll /r:System.Windows.Forms.dll "/out:$buildDir\Sai2Rec.exe" $sources
if ($LASTEXITCODE -ne 0) {
    throw "主程序编译失败，退出码：$LASTEXITCODE"
}

Write-Output "BUILD-OK $buildDir\Sai2Rec.exe"
