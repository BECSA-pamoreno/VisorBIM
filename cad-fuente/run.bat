@echo off
REM ===== Visor CAD BECSA - compilar y probar en local =====
REM Ajusta esta ruta si tu Node portable esta en otro sitio:
set NODE_DIR=D:\Personal\Externo\#BECSA\Automatizacoines\Windows\node-v22.23.2-win-x64
set PATH=%NODE_DIR%;%PATH%

cd /d "%~dp0"

where node >nul 2>nul
if errorlevel 1 (
  echo [ERROR] No encuentro node.exe en %NODE_DIR%
  pause
  exit /b 1
)

if not exist node_modules (
  echo Instalando dependencias...
  call npm install
  if errorlevel 1 (
    echo [ERROR] Fallo npm install
    pause
    exit /b 1
  )
)

echo Compilando...
call npm run build
if errorlevel 1 (
  echo [ERROR] Fallo la compilacion
  pause
  exit /b 1
)

echo.
echo Abriendo http://localhost:4173 ... (cierra esta ventana para parar)
start "" http://localhost:4173
call npx vite preview --port 4173
pause
