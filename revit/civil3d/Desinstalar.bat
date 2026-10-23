@echo off
REM  BECSATools - Desinstalar el exportador al Visor BIM de Civil 3D 2027
setlocal
set "DESTINO=%APPDATA%\Autodesk\ApplicationPlugins\BecsaVisorC3D.bundle"

tasklist /FI "IMAGENAME eq acad.exe" 2>nul | find /I "acad.exe" >nul
if not errorlevel 1 (
  echo Civil 3D esta abierto: cierralo antes de desinstalar.
  pause
  exit /b 1
)

if exist "%DESTINO%" (
  rmdir /s /q "%DESTINO%"
  echo Desinstalado: %DESTINO%
) else (
  echo No estaba instalado en %DESTINO%
)
pause
