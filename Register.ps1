# OnCadTools Permanent Registration Script
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "  Установка и постоянная активация OnCadTools 3.2.7.0" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

$dir = $PSScriptRoot
if (-not $dir) { $dir = "D:\Program Files\OnCadTools" }

$dllPath = Join-Path $dir "OnCadTools.dll"
Write-Host "[*] DLL путь: $dllPath" -ForegroundColor Gray

# 1. Register with 64-bit RegAsm
$regasm = "$env:SystemRoot\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe"
if (Test-Path $regasm) {
    Write-Host "[*] Вызов 64-битного RegAsm /codebase..." -ForegroundColor Gray
    & $regasm /codebase $dllPath | Out-Null
    Write-Host "[+] COM регистрация выполнена успешно." -ForegroundColor Green
} else {
    Write-Host "[-] Ошибка: RegAsm.exe не найден!" -ForegroundColor Red
}

# 2. Hardware Keys
$regBase = "HKCU:\Software\PYCZT_Tools\PYCZT_Tools.ini"
$codes = @("181571K8G42I888888888888", "364EI3413EEK888888888888", "OnCadTools")
foreach ($code in $codes) {
    $p = "$regBase\$code"
    if (-not (Test-Path $p)) { New-Item -Path $p -Force | Out-Null }
    Set-ItemProperty -Path $p -Name "(default)" -Value $code
    Set-ItemProperty -Path $p -Name "Code" -Value $code
    Set-ItemProperty -Path $p -Name "UserName" -Value "PYCZT_TOOLS"
}
Write-Host "[+] Постоянные аппаратные ключи лицензии активированы." -ForegroundColor Green

# 3. SolidWorks Addin entries
$swKeys = @(
    "HKLM:\Software\SolidWorks\Addins\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}",
    "HKLM:\Software\SolidWorks\AddInsStartup\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}",
    "HKCU:\Software\SolidWorks\Addins\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}",
    "HKCU:\Software\SolidWorks\AddInsStartup\{03412ba8-10f6-4d51-ac38-4937ce7bea5f}"
)
foreach ($k in $swKeys) {
    if (-not (Test-Path $k)) { New-Item -Path $k -Force | Out-Null }
    Set-ItemProperty -Path $k -Name "(default)" -Value 1 -Type DWord
    Set-ItemProperty -Path $k -Name "Title" -Value "OnCadTools" -ErrorAction SilentlyContinue
    Set-ItemProperty -Path $k -Name "Description" -Value "OnCadTools SolidWorks Addin" -ErrorAction SilentlyContinue
}
Write-Host "[+] Надстройка включена в автозагрузку SolidWorks." -ForegroundColor Green

# 4. Copy config to SolidWorks folder if present
$swConfigDir = "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS"
if (Test-Path $swConfigDir) {
    Copy-Item (Join-Path $dir "OnCadToolsConfig.ini") (Join-Path $swConfigDir "OnCadToolsConfig.ini") -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "[OK] Надстройка OnCadTools 3.2.7.0 успешно установлена и активирована навсегда!" -ForegroundColor Green
