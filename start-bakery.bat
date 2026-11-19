@echo off
setlocal
title RJAM Bakery Management & POS Server

echo ============================================================
echo   RJAM BAKERY MANAGEMENT ^& POS SERVER (LOCAL WI-FI / LAN)
echo ============================================================
echo.

cd /d "%~dp0"

echo Checking MySQL / MariaDB on port 3306...
powershell -NoProfile -Command "try { $c = New-Object System.Net.Sockets.TcpClient('127.0.0.1', 3306); $c.Close(); exit 0 } catch { exit 1 }"
if errorlevel 1 (
    echo [WARNING] MySQL / MariaDB is NOT listening on 127.0.0.1:3306.
    echo           Please open XAMPP Control Panel and start MySQL first!
    echo.
    set /p CONT="Press ENTER after starting MySQL (or close this window to cancel)... "
) else (
    echo [OK] MySQL / MariaDB detected on port 3306.
)

echo.
echo Open RJAM Bakery on your devices using:
echo   This PC:       http://localhost:5085
for /f "usebackq delims=" %%i in (`powershell -NoProfile -Command "Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.PrefixOrigin -ne 'WellKnown' -and $_.InterfaceAlias -notlike 'vEthernet*' } | ForEach-Object { 'http://' + $_.IPAddress + ':5085 (' + $_.InterfaceAlias + ')' }"`) do (
    echo   Tablet/Phone:  %%i
)
echo.
echo Tip: Keep this window open while the bakery is operating.
echo      Press Ctrl+C to stop the server cleanly.
echo ============================================================
echo.

echo Stopping any previous RJAM Bakery server instance...
taskkill /F /IM RjamBakery.Web.exe >nul 2>&1

dotnet run --project src\RjamBakery.Web --launch-profile lan

if errorlevel 1 (
    echo.
    echo [ERROR] RJAM Bakery server stopped with an error.
    pause
)
