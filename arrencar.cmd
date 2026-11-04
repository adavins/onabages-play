@echo off
rem Arrenca Ona Bages Play en mode de desenvolupament.
rem 1. Compila el motor (perque l'aplicacio faci servir sempre l'ultima versio).
rem 2. Obre la interficie (npm start, des de la carpeta app).
rem Es pot executar amb doble clic o des d'una consola a l'arrel del projecte.

cd /d "%~dp0"

echo Compilant el motor...
dotnet build engine\OnaBages.Engine -v q -nologo
if errorlevel 1 (
    echo.
    echo AVIS: no s'ha pogut compilar el motor.
    echo Si el motor ja esta en marxa, els seus fitxers estan en us i no es poden substituir:
    echo l'aplicacio fara servir el motor que ja funciona. Per tenir l'ultima versio,
    echo tanca l'aplicacio amb "Aturar-ho tot" i torna a executar aquest script.
    echo.
)

cd app
if not exist node_modules (
    echo Instal.lant les dependencies de la interficie...
    call npm install
)
call npm start
