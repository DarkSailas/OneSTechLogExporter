# PowerShell-скрипт установки/обновления службы OneSTechLogExporter
$ErrorActionPreference = "Stop"

$serviceName = "OneSTechLogExporter"
$displayName = "OneSTechLogExporter"
$description = "Сервис обмена 1С с Elasticsearch"

# Проверка прав администратора
$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "[FATAL] Запустите скрипт от имени Администратора!" -ForegroundColor Red
    exit 1
}

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$exePath = Join-Path $scriptDir "OneSTechLogExporter.Service.exe"

if (-not (Test-Path $exePath)) {
    $exePath = Join-Path (Split-Path -Parent $scriptDir) "OneSTechLogExporter.Service.exe"
}

if (-not (Test-Path $exePath)) {
    Write-Host "[FATAL] Исполняемый файл службы $exePath не найден!" -ForegroundColor Red
    exit 1
}

$exePath = [System.IO.Path]::GetFullPath($exePath)
$binPath = '"' + $exePath + '"'

$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService) {
    Write-Host "Обновление параметров службы $serviceName..." -ForegroundColor Yellow
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1

    $res = sc.exe config $serviceName binPath= $binPath DisplayName= $displayName start= auto 2>&1
    if ($LASTEXITCODE -ne 0 -or $res -match "отмечена для удаления") {
        Write-Host "Служба удерживается процессом mmc (services.msc). Закрываем окно Службы..." -ForegroundColor Yellow
        Stop-Process -Name mmc -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        sc.exe delete $serviceName | Out-Null
        Start-Sleep -Seconds 2
        New-Service -Name $serviceName -BinaryPathName $binPath -DisplayName $displayName -Description $description -StartupType Automatic | Out-Null
    } else {
        sc.exe description $serviceName $description | Out-Null
    }
} else {
    Write-Host "Первичная установка службы $serviceName из $exePath..." -ForegroundColor Yellow
    $res = New-Service -Name $serviceName -BinaryPathName $binPath -DisplayName $displayName -Description $description -StartupType Automatic 2>&1
    if ($LASTEXITCODE -ne 0 -or $res -match "отмечена для удаления") {
        Write-Host "Освобождение зависшего дескриптора службы (закрытие services.msc)..." -ForegroundColor Yellow
        Stop-Process -Name mmc -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        sc.exe delete $serviceName | Out-Null
        Start-Sleep -Seconds 2
        New-Service -Name $serviceName -BinaryPathName $binPath -DisplayName $displayName -Description $description -StartupType Automatic | Out-Null
    }
}

sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

Write-Host "Служба $serviceName успешно зарегистрирована и настроена!" -ForegroundColor Green

Write-Host "Запуск службы $serviceName..." -ForegroundColor Yellow
Start-Service -Name $serviceName -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$running = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($running -and $running.Status -eq 'Running') {
    Write-Host "Служба $serviceName успешно ЗАПУЩЕНА и работает!" -ForegroundColor Green
} else {
    Write-Host "Служба зарегистрирована. Для запуска выполните: Start-Service $serviceName" -ForegroundColor Cyan
}