@echo off
REM ===================================================================
REM  Atalho de um clique: espelha a tela do notebook no PC principal.
REM  Edite a linha ALVO abaixo com o nome (ou IP) do seu notebook.
REM ===================================================================

set ALVO=NOTE-HALLAN

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0conectar.ps1" -Alvo %ALVO%

if errorlevel 1 (
    echo.
    pause
)
