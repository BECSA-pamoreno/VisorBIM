@echo off
REM ======================================================================
REM  BECSATools - Exportador al Visor BIM para Civil 3D 2027  v1.0
REM  Doble clic y hace todo:
REM    1. Comprueba Civil 3D 2027
REM    2. Si falta, descarga el SDK de .NET 10 (zip portable, sin admin, solo la 1a vez)
REM       en %USERPROFILE%\dotnet  (el mismo que usa BecsaTerrenoC3D)
REM    3. Compila y monta BecsaVisorC3D.bundle
REM    4. Lo instala en %APPDATA%\Autodesk\ApplicationPlugins (espera a que cierres Civil 3D)
REM ======================================================================
setlocal
cd /d "%~dp0"

set "ACAD=C:\Program Files\Autodesk\AutoCAD 2027"
set "SDKDIR=%USERPROFILE%\dotnet"
set "DOTNET=%USERPROFILE%\dotnet\dotnet.exe"
set "URLSDK=https://aka.ms/dotnet/10.0/dotnet-sdk-win-x64.zip"
set "ZIP=%TEMP%\dotnet-sdk-10-win-x64.zip"
set "BUNDLE=BecsaVisorC3D.bundle"
set "DESTINO=%APPDATA%\Autodesk\ApplicationPlugins\BecsaVisorC3D.bundle"
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_NOLOGO=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

echo.
echo ==== 1/4  Civil 3D 2027 ====
if not exist "%ACAD%\acdbmgd.dll" goto err_acad
if not exist "%ACAD%\AdWindows.dll" goto err_acad
if not exist "%ACAD%\C3D\AeccDbMgd.dll" goto err_acad
echo OK: %ACAD%

echo.
echo ==== 2/4  SDK de .NET 10 ====
call :tiene_sdk
if not errorlevel 1 goto sdk_ok
echo No esta instalado. Se descarga UNA sola vez (unos 250 MB, tarda unos minutos)...
echo Destino: %SDKDIR%
if exist "%ZIP%" del /q "%ZIP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; try { [Net.WebRequest]::DefaultWebProxy.Credentials=[Net.CredentialCache]::DefaultNetworkCredentials } catch {}; Invoke-WebRequest -UseBasicParsing -Uri $env:URLSDK -OutFile $env:ZIP"
if not exist "%ZIP%" curl.exe -L --fail -o "%ZIP%" "%URLSDK%"
if not exist "%ZIP%" goto err_descarga
echo Descomprimiendo...
if not exist "%SDKDIR%" mkdir "%SDKDIR%"
tar -xf "%ZIP%" -C "%SDKDIR%"
if errorlevel 1 powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -Force -Path $env:ZIP -DestinationPath $env:SDKDIR"
del /q "%ZIP%"
call :tiene_sdk
if errorlevel 1 goto err_sdk
:sdk_ok
echo OK: %DOTNET%

echo.
echo ==== 3/4  Compilando ====
if exist "salida\BecsaVisorC3D.dll" del /q "salida\BecsaVisorC3D.dll"
"%DOTNET%" build BecsaVisorC3D.csproj -c Release -o salida "-p:AcadDir=%ACAD%"
if errorlevel 1 goto err_compilar
if not exist "salida\BecsaVisorC3D.dll" goto err_compilar
if exist "%BUNDLE%" rmdir /s /q "%BUNDLE%"
mkdir "%BUNDLE%\Contents"
copy /y "PackageContents.xml" "%BUNDLE%\" >nul
copy /y "salida\BecsaVisorC3D.dll" "%BUNDLE%\Contents\" >nul
if errorlevel 1 goto err_bundle
echo OK: %~dp0%BUNDLE%

echo.
echo ==== 4/4  Instalando ====
:comprobar_c3d
tasklist /FI "IMAGENAME eq acad.exe" 2>nul | find /I "acad.exe" >nul
if errorlevel 1 goto instalar
echo Civil 3D esta abierto. Cierralo y pulsa una tecla para instalar.
echo (Si prefieres no instalar ahora, cierra esta ventana: el bundle ya esta compilado.)
pause >nul
goto comprobar_c3d
:instalar
if not exist "%APPDATA%\Autodesk\ApplicationPlugins" mkdir "%APPDATA%\Autodesk\ApplicationPlugins"
if exist "%DESTINO%" rmdir /s /q "%DESTINO%"
xcopy "%BUNDLE%" "%DESTINO%\" /e /i /y /q >nul
if errorlevel 1 goto err_instalar

echo.
echo ================================================================
echo  Instalado en %DESTINO%
echo  Abre Civil 3D 2027: pestana BECSATools, panel "Visor BIM".
echo  Si sale el aviso de seguridad al cargar, elige "Cargar siempre".
echo  Si el panel no aparece, escribe BECSA_CINTA_VISOR.
echo ================================================================
pause
exit /b 0

REM ---------------------------------------------------------------------
:tiene_sdk
if not exist "%DOTNET%" exit /b 1
"%DOTNET%" --list-sdks 2>nul | findstr /b /l "10." >nul
exit /b %errorlevel%

:err_acad
echo.
echo *** No encuentro Civil 3D 2027 en "%ACAD%". ***
echo Edita este .bat y cambia la linea "set ACAD=..." por tu carpeta de instalacion.
pause
exit /b 1

:err_descarga
echo.
echo *** No se pudo descargar el SDK de .NET 10 (la red puede bloquearlo). ***
echo Descargalo en otro equipo: %URLSDK%
echo y descomprimelo en %SDKDIR% (tiene que quedar %SDKDIR%\dotnet.exe). Luego vuelve a ejecutar este .bat.
pause
exit /b 1

:err_sdk
echo.
echo *** El SDK de .NET 10 no ha quedado bien en %SDKDIR%. ***
echo Borra esa carpeta y vuelve a ejecutar este .bat.
pause
exit /b 1

:err_compilar
echo.
echo *** ERROR al compilar. Copia el texto de arriba y pasaselo a Claude. ***
pause
exit /b 1

:err_bundle
echo.
echo *** No se pudo montar el bundle. ***
pause
exit /b 1

:err_instalar
echo.
echo *** Error al copiar a "%DESTINO%" ***
pause
exit /b 1
