# PowerShell Script for OneSTechLogExporter Build, Test & Distribution
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$tempPublishDir = Join-Path $env:LOCALAPPDATA "Temp\OneSTechLogExporterPublish"

$publishServiceDir = Join-Path $root "publish\Service"
$publishGuiDir = Join-Path $root "publish\Gui"

Write-Host ""
Write-Host "  OneSTechLogExporter Build Script" -ForegroundColor Cyan
Write-Host "  ================================" -ForegroundColor Cyan
Write-Host ""

# --- [1/5] Cleanup ---
Write-Host "[1/5] Stopping running processes..." -ForegroundColor Yellow

Get-Process | Where-Object Name -Match "OneSTechLogExporter" | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

if (Test-Path $tempPublishDir) {
    Remove-Item -Path $tempPublishDir -Recurse -Force -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Path $publishServiceDir -Force | Out-Null
New-Item -ItemType Directory -Path $publishGuiDir -Force | Out-Null
Write-Host "       OK" -ForegroundColor Green

# Проверка наличия .NET SDK на сервере
$sdkCheck = & dotnet --list-sdks 2>$null
if (-not $sdkCheck) {
    Write-Host ""
    Write-Host "[FATAL] На текущем сервере не установлен .NET SDK (необходим для сборки из исходников)." -ForegroundColor Red
    Write-Host "Сборка из исходного кода НЕ ТРЕБУЕТСЯ! Готовый собранный дистрибутив находится в папках:" -ForegroundColor Yellow
    Write-Host "  - Служба Windows: .\publish\Service\OneSTechLogExporter.Service.exe" -ForegroundColor Cyan
    Write-Host "  - Графическая GUI: .\publish\Gui\OneSTechLogExporter.Gui.exe" -ForegroundColor Cyan
    Write-Host "Для установки службы выполните от Администратора:" -ForegroundColor Yellow
    Write-Host "  powershell -ExecutionPolicy Bypass -File .\publish\Service\scripts\INSTALL_SERVICE.ps1" -ForegroundColor Green
    Write-Host ""
    exit 1
}

# --- [2/5] Restore ---
Write-Host "[2/5] dotnet restore..." -ForegroundColor Yellow
$slnPath = Join-Path $root "OneSTechLogExporter.slnx"
& dotnet restore $slnPath --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] dotnet restore failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "       OK" -ForegroundColor Green

# --- [3/5] Build ---
Write-Host "[3/5] dotnet build (Release)..." -ForegroundColor Yellow
& dotnet build $slnPath -c Release --no-restore --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] Build failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}
Write-Host "       OK" -ForegroundColor Green

# --- [4/5] Tests ---
Write-Host "[4/5] dotnet test (Unit tests)..." -ForegroundColor Yellow
$testProject = Join-Path $root "tests\OneSTechLogExporter.Tests\OneSTechLogExporter.Tests.csproj"
& dotnet test $testProject -c Release --no-build --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "[WARN] Some tests failed." -ForegroundColor Yellow
} else {
    Write-Host "       OK" -ForegroundColor Green
}

# --- [5/5] Publish Separate Service & GUI Packages ---
Write-Host "[5/5] Publishing Service to ./publish/Service and GUI to ./publish/Gui..." -ForegroundColor Yellow

$serviceProject = Join-Path $root "src\OneSTechLogExporter.Service\OneSTechLogExporter.Service.csproj"
$guiProject = Join-Path $root "src\OneSTechLogExporter.Gui\OneSTechLogExporter.Gui.csproj"

$tempService = Join-Path $tempPublishDir "Service"
$tempGui = Join-Path $tempPublishDir "Gui"

# Publish Service to Temp
& dotnet publish $serviceProject -c Release -p:UseAppHost=true --self-contained false -o $tempService --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] Service Publish failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}

# Publish GUI to Temp
& dotnet publish $guiProject -c Release -p:UseAppHost=true --self-contained false -o $tempGui --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FATAL] GUI Publish failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}

# Copy all non-EXE files using robocopy
& robocopy $tempService $publishServiceDir /E /XF *.exe /NJH /NJS /NDL /NC /NS /NP | Out-Null
& robocopy $tempGui $publishGuiDir /E /XF *.exe /NJH /NJS /NDL /NC /NS /NP | Out-Null

# Copy Install Scripts into Service publish
$serviceScriptsDir = Join-Path $publishServiceDir "scripts"
New-Item -ItemType Directory -Path $serviceScriptsDir -Force | Out-Null
& robocopy (Join-Path $root "scripts") $serviceScriptsDir /E /NJH /NJS /NDL /NC /NS /NP | Out-Null

# Copy AppSettings and Icon files
Copy-Item -Path (Join-Path $root "src\OneSTechLogExporter.Service\appsettings.json") -Destination $publishGuiDir -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $root "src\OneSTechLogExporter.Service\icon.ico") -Destination $publishServiceDir -Force -ErrorAction SilentlyContinue
Copy-Item -Path (Join-Path $root "src\OneSTechLogExporter.Gui\icon.ico") -Destination $publishGuiDir -Force -ErrorAction SilentlyContinue

# Atomic swap for .exe binaries to bypass Google Drive VFS locks
$svcTmp = Join-Path $publishServiceDir "OneSTechLogExporter.Service.exe.tmp"
$svcFinal = Join-Path $publishServiceDir "OneSTechLogExporter.Service.exe"
Copy-Item -Path (Join-Path $tempService "OneSTechLogExporter.Service.exe") -Destination $svcTmp -Force
if (Test-Path $svcFinal) { Remove-Item $svcFinal -Force -ErrorAction SilentlyContinue }
Move-Item -Path $svcTmp -Destination $svcFinal -Force

$guiTmp = Join-Path $publishGuiDir "OneSTechLogExporter.Gui.exe.tmp"
$guiFinal = Join-Path $publishGuiDir "OneSTechLogExporter.Gui.exe"
Copy-Item -Path (Join-Path $tempGui "OneSTechLogExporter.Gui.exe") -Destination $guiTmp -Force
if (Test-Path $guiFinal) { Remove-Item $guiFinal -Force -ErrorAction SilentlyContinue }
Move-Item -Path $guiTmp -Destination $guiFinal -Force

Write-Host "       OK" -ForegroundColor Green
Write-Host ""
Write-Host "  ==============================" -ForegroundColor Cyan
Write-Host "  BUILD SUCCESSFUL" -ForegroundColor Green
Write-Host "  Service Output: $publishServiceDir" -ForegroundColor Cyan
Write-Host "  GUI Output:     $publishGuiDir" -ForegroundColor Cyan
Write-Host "  ==============================" -ForegroundColor Cyan
Write-Host ""
