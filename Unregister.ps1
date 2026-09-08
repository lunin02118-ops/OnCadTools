# OnCadTools Unregister Script
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$dir = $PSScriptRoot
if (-not $dir) { $dir = "D:\Program Files\OnCadTools" }

$dllPath = Join-Path $dir "OnCadTools.dll"
$regasm = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"

if (Test-Path $regasm) {
    & $regasm /unregister $dllPath | Out-Null
    Write-Host "[+] COM регистрация удалена." -ForegroundColor Green
}

$swKeys = @(
    "HKLM:\Software\SolidWorks\Addins\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}",
    "HKLM:\Software\SolidWorks\AddInsStartup\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}",
    "HKCU:\Software\SolidWorks\Addins\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}",
    "HKCU:\Software\SolidWorks\AddInsStartup\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}"
)
foreach ($k in $swKeys) {
    if (Test-Path $k) { Remove-Item -Path $k -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host "[+] Записи SolidWorks удалены." -ForegroundColor Green
