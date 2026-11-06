@echo off
rem Genera la versio portable d'Ona Bages Play (D-044, pas 11 de la fase 2):
rem   build\OnaBagesPlay\            carpeta a punt per fer servir
rem   build\OnaBagesPlay-<versio>.zip  el mateix, comprimit per distribuir-lo
rem
rem 1. Publica el motor i l'eina de recuperacio amb el .NET inclos (no cal instal.lar-lo).
rem 2. Empaqueta la interficie amb @electron/packager i hi afegeix el motor (resources\engine).
rem 3. Hi posa la carpeta "dades" (mode portable) i les instruccions, i ho comprimeix.
rem
rem Cal tenir l'aplicacio tancada si fa servir el motor de build\ (no el de desenvolupament).

setlocal
cd /d "%~dp0"

for /f "delims=" %%v in ('node -p "require('./app/package.json').version"') do set VERSION=%%v
if "%VERSION%"=="" (
    echo ERROR: no s'ha pogut llegir la versio d'app\package.json. Cal tenir Node.js instal.lat.
    exit /b 1
)
echo Empaquetant Ona Bages Play %VERSION%...

if exist build rmdir /s /q build
if exist build (
    echo ERROR: no s'ha pogut esborrar la carpeta build. Potser l'aplicacio empaquetada esta oberta.
    exit /b 1
)

echo.
echo [1/4] Publicant el motor...
rem Sense fitxers de depuracio (.pdb): no calen per fer servir l'aplicacio.
dotnet publish engine\OnaBages.Engine -c Release -r win-x64 --self-contained -p:DebugType=none -o build\engine -v q -nologo
if errorlevel 1 goto :error
dotnet publish engine\OnaBages.Recovery -c Release -r win-x64 --self-contained -p:DebugType=none -o build\engine -v q -nologo
if errorlevel 1 goto :error

echo.
echo [2/4] Empaquetant la interficie...
cd app
if not exist node_modules call npm install
call npx electron-packager . OnaBagesPlay --platform=win32 --arch=x64 --out=..\build --overwrite ^
    --extra-resource=..\build\engine ^
    --icon=assets\icona.ico ^
    --app-version=%VERSION% ^
    --win32metadata.ProductName="Ona Bages Play" ^
    --win32metadata.FileDescription="Ona Bages Play" ^
    --win32metadata.CompanyName="Ona Bages"
if errorlevel 1 (
    cd ..
    goto :error
)
cd ..
move build\OnaBagesPlay-win32-x64 build\OnaBagesPlay >nul
if errorlevel 1 goto :error

rem Idiomes de Chromium: nomes catala, castella i angles (la resta ocupa uns 45 MB i no es fa servir).
for %%f in (build\OnaBagesPlay\locales\*.pak) do (
    if /i not "%%~nf"=="ca" if /i not "%%~nf"=="es" if /i not "%%~nf"=="en-US" del "%%f"
)

echo.
echo [3/4] Carpeta de dades, instruccions i documentacio...
mkdir build\OnaBagesPlay\dades
copy /y tools\portable\dades-LLEGEIX-ME.txt build\OnaBagesPlay\dades\LLEGEIX-ME.txt >nul
copy /y tools\portable\LLEGEIX-ME.txt build\OnaBagesPlay\LLEGEIX-ME.txt >nul
rem Documentacio (D-092): el boto "?" de l'aplicacio obre aquests fitxers al navegador.
mkdir build\OnaBagesPlay\documentacio
copy /y docs\manual-usuari.html build\OnaBagesPlay\documentacio\ >nul
if errorlevel 1 goto :error
copy /y docs\documentacio-tecnica.html build\OnaBagesPlay\documentacio\ >nul
if errorlevel 1 goto :error

echo.
echo [4/4] Comprimint...
powershell -NoProfile -Command "Compress-Archive -Path 'build\OnaBagesPlay' -DestinationPath 'build\OnaBagesPlay-%VERSION%.zip' -Force"
if errorlevel 1 goto :error

echo.
echo Fet: build\OnaBagesPlay-%VERSION%.zip
exit /b 0

:error
echo.
echo ERROR: no s'ha pogut empaquetar. Mira els missatges de sobre.
exit /b 1
