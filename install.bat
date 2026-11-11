@echo off
setlocal enabledelayedexpansion

net session >nul 2>&1
if errorlevel 1 (
    echo This installer installs machine-wide, for every user and service account,
    echo so it needs administrator rights. Run it again as administrator.
    exit /b 1
)

echo Publishing: dotnet publish src\SecurityToolkit.Cli -r win-x64 -p:SelfContained=true ...
dotnet publish src\SecurityToolkit.Cli -c Release -r win-x64 -p:SelfContained=true
if errorlevel 1 (
    echo Publish failed, installation aborted.
    exit /b 1
)

set "TARGET=%ProgramFiles%\SecurityToolkit\bin"
if not exist "%TARGET%" mkdir "%TARGET%"

copy /Y "src\SecurityToolkit.Cli\bin\Release\net11.0\win-x64\publish\security-toolkit.exe" "%TARGET%\security-toolkit.exe" >nul
if errorlevel 1 (
    echo Copy failed: %TARGET%\security-toolkit.exe
    exit /b 1
)
echo Installed: %TARGET%\security-toolkit.exe

set "SYSTEMPATH="
for /f "usebackq tokens=2,*" %%A in (`reg query "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment" /v PATH 2^>nul`) do set "SYSTEMPATH=%%B"

echo ;%SYSTEMPATH%; | find /I ";%TARGET%;" >nul
if errorlevel 1 (
    if defined SYSTEMPATH (
        setx PATH "%SYSTEMPATH%;%TARGET%" /M >nul
    ) else (
        setx PATH "%TARGET%" /M >nul
    )
    if errorlevel 1 (
        echo Updating PATH failed. Check whether the system PATH exceeds setx's 1024-character
        echo limit - if it does, extend PATH by hand in System Properties,
        echo Environment Variables.
        exit /b 1
    )
    echo System PATH updated. Every process that is already running has to be restarted
    echo for the change to take effect: open a new terminal, and restart every Windows
    echo service as well, for example the Azure DevOps agent service.
) else (
    echo The system PATH already contains: %TARGET%
)

rem --- Machine-level, project-independent infrastructure (optional) ---
rem Switches: --sonar, --mssql (unattended), --no-infra (skip). Without a switch it asks.
set "INFRA_ARGS="
set "ASK_INFRA=1"
for %%A in (%*) do (
    if /I "%%~A"=="--sonar" (set "INFRA_ARGS=!INFRA_ARGS! --sonar" & set "ASK_INFRA=0")
    if /I "%%~A"=="--mssql" (set "INFRA_ARGS=!INFRA_ARGS! --mssql" & set "ASK_INFRA=0")
    if /I "%%~A"=="--no-infra" set "ASK_INFRA=0"
)

if "%ASK_INFRA%"=="1" (
    echo.
    echo Machine-level infrastructure (needs Docker Desktop; usually skipped on a developer machine^):
    choice /C YN /N /M "Install/update the shared SonarQube + PostgreSQL on this machine? [Y/N] "
    if !errorlevel! EQU 1 set "INFRA_ARGS=!INFRA_ARGS! --sonar"
    choice /C YN /N /M "Install/update the shared test SQL Server (MSSQL) on this machine? [Y/N] "
    if !errorlevel! EQU 1 set "INFRA_ARGS=!INFRA_ARGS! --mssql"
)

if defined INFRA_ARGS (
    echo.
    echo Infrastructure: security-toolkit infra install!INFRA_ARGS!
    "%TARGET%\security-toolkit.exe" infra install!INFRA_ARGS!
    if errorlevel 1 (
        echo Installing the infrastructure failed, see the messages above. The CLI installation is still valid.
        exit /b 2
    )
)

echo.
echo Done. From a new terminal (or a restarted service): security-toolkit --help
endlocal
