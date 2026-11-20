# 下载 nestest 的 ROM 与官方日志到 .testdata/。
# 这些文件不随本项目分发，仅用于本地 CPU 对拍测试。
# 来源：https://github.com/christopherpow/nes-test-roms

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root '.testdata'
New-Item -ItemType Directory -Force -Path $target | Out-Null

$base = 'https://raw.githubusercontent.com/christopherpow/nes-test-roms/master/other'
$files = @{
    'nestest.nes' = "$base/nestest.nes"
    'nestest.log' = "$base/nestest.log"
}

foreach ($name in $files.Keys) {
    $path = Join-Path $target $name
    Write-Host "下载 $name ..."
    Invoke-WebRequest -Uri $files[$name] -OutFile $path
    $size = (Get-Item $path).Length
    Write-Host "  -> $path ($size 字节)"
}

Write-Host ''
Write-Host '完成。运行测试：'
Write-Host '  dotnet run --project tests/NesEmulator.Tests'
