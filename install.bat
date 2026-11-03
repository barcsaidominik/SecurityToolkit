@echo off
setlocal enabledelayedexpansion

net session >nul 2>&1
if errorlevel 1 (
    echo Ez a telepito rendszerszinten telepit, minden felhasznalo/szolgaltatas-fiok szamara,
    echo ezert rendszergazdai jogosultsag kell hozza. Inditsd ujra rendszergazdakent.
    exit /b 1
)

echo Publikalas: dotnet publish src\SecurityToolkit.Cli -r win-x64 -p:SelfContained=true ...
dotnet publish src\SecurityToolkit.Cli -c Release -r win-x64 -p:SelfContained=true
if errorlevel 1 (
    echo Publikalas sikertelen, telepites megszakitva.
    exit /b 1
)

set "TARGET=%ProgramFiles%\SecurityToolkit\bin"
if not exist "%TARGET%" mkdir "%TARGET%"

copy /Y "src\SecurityToolkit.Cli\bin\Release\net10.0\win-x64\publish\security-toolkit.exe" "%TARGET%\security-toolkit.exe" >nul
if errorlevel 1 (
    echo Masolas sikertelen: %TARGET%\security-toolkit.exe
    exit /b 1
)
echo Telepitve: %TARGET%\security-toolkit.exe

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
        echo PATH frissites sikertelen. Ellenorizd, hogy a rendszerszintu PATH nem lepi-e tul
        echo a setx 1024 karakteres korlatjat - ha igen, a PATH-ot kezzel, a Rendszerbeallitasok
        echo Kornyezeti valtozok parbeszedeben kell bovitened.
        exit /b 1
    )
    echo PATH frissitve rendszerszinten. Minden mar futo folyamatnak ujra kell indulnia,
    echo hogy a valtozas ervenybe lepjen: nyiss uj terminalt, es inditsd ujra minden
    echo Windows szolgaltatast is, peldaul az Azure DevOps agent szolgaltatasat.
) else (
    echo A rendszerszintu PATH mar tartalmazza: %TARGET%
)

rem --- Gepi szintu, projektfuggetlen infrastruktura (opcionalis) ---
rem Kapcsolok: --sonar, --mssql (felugyelet nelkul), --no-infra (kihagyas). Kapcsolo nelkul kerdez.
set "INFRA_ARGS="
set "ASK_INFRA=1"
for %%A in (%*) do (
    if /I "%%~A"=="--sonar" (set "INFRA_ARGS=!INFRA_ARGS! --sonar" & set "ASK_INFRA=0")
    if /I "%%~A"=="--mssql" (set "INFRA_ARGS=!INFRA_ARGS! --mssql" & set "ASK_INFRA=0")
    if /I "%%~A"=="--no-infra" set "ASK_INFRA=0"
)

if "%ASK_INFRA%"=="1" (
    echo.
    echo Gepi szintu infrastruktura (Docker Desktop kell hozza; fejlesztoi gepen altalaban kihagyhato^):
    choice /C IN /N /M "Kozos SonarQube + PostgreSQL telepitese/frissitese ezen a gepen? [I/N] "
    if !errorlevel! EQU 1 set "INFRA_ARGS=!INFRA_ARGS! --sonar"
    choice /C IN /N /M "Kozos teszt-SQL Server (MSSQL) telepitese/frissitese ezen a gepen? [I/N] "
    if !errorlevel! EQU 1 set "INFRA_ARGS=!INFRA_ARGS! --mssql"
)

if defined INFRA_ARGS (
    echo.
    echo Infrastruktura: security-toolkit infra install!INFRA_ARGS!
    "%TARGET%\security-toolkit.exe" infra install!INFRA_ARGS!
    if errorlevel 1 (
        echo Az infrastruktura telepitese sikertelen, lasd a fenti uzeneteket. A CLI telepitese ettol meg ervenyes.
        exit /b 2
    )
)

echo.
echo Kesz. Uj terminalbol (vagy ujrainditott szolgaltatasbol): security-toolkit --help
endlocal
