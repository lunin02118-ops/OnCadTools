# Скрипт сборки и регистрации OnCadTools Russian Pro
$ErrorActionPreference = "Stop"
$baseDir = $PSScriptRoot
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$regasm = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"

Write-Host "=== Сборка OnCadTools.dll ===" -ForegroundColor Cyan
& $csc /target:library "/r:$baseDir\SolidWorks.Interop.sldworks.dll" "/r:$baseDir\SolidWorks.Interop.swpublished.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:"$baseDir\OnCadTools.dll" "$baseDir\src\SimpleAddinWrapper.cs"

if ($LASTEXITCODE -eq 0) {
    Write-Host "Сборка успешно завершена!" -ForegroundColor Green
    Write-Host "=== Регистрация плагина ===" -ForegroundColor Cyan
    & $regasm /codebase /tlb "$baseDir\OnCadTools.dll"
    Write-Host "Плагин успешно зарегистрирован!" -ForegroundColor Green
} else {
    Write-Host "Ошибка сборки OnCadTools.dll!" -ForegroundColor Red
}
