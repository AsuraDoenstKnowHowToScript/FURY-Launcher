@echo off
setlocal enabledelayedexpansion
cd /d "%~dp0"
title FURY Launcher

echo ==================================================
echo   FURY Launcher  -  build ^& execucao
echo ==================================================
echo.

rem  Uso:
rem    run.bat            -> instala dependencias, compila (Release) e roda
rem    run.bat publish    -> gera build distribuivel (win-x64, self-contained) em dist\
set "MODE=%~1"

rem ---------- 1) Dependencia: .NET 8 SDK ----------
call :ensure_dotnet
if errorlevel 1 goto :end

rem ---------- 2) Restaurar pacotes (NuGet: Avalonia, CmlLib, ...) ----------
echo [1/3] Restaurando dependencias (NuGet)...
dotnet restore "FURY.sln" --nologo
if errorlevel 1 goto :build_fail

if /i "%MODE%"=="publish" goto :publish

rem ---------- 3) Compilar ----------
echo [2/3] Compilando (Release)...
dotnet build "FURY.sln" -c Release --nologo
if errorlevel 1 goto :build_fail

rem ---------- 4) Rodar ----------
echo [3/3] Iniciando FURY Launcher...
echo.
rem WinExe nao anexa console: redireciona stderr p/ log, senao um crash some sem mensagem.
"Launcher.App\bin\Release\net8.0\FURY Launcher.exe" 2>"%~dp0crash.log"
if errorlevel 1 (
  echo.
  echo [ERRO] O aplicativo terminou com erro. Detalhes ^(crash.log^):
  type "%~dp0crash.log"
  pause
)
goto :end

rem ================== PUBLISH (build distribuivel) ==================
:publish
echo [2/2] Publicando build distribuivel ^(win-x64, self-contained^)...
set "OUT=dist\win-x64"
if exist "%OUT%" rmdir /s /q "%OUT%"
dotnet publish "Launcher.App\Launcher.App.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o "%OUT%" --nologo
if errorlevel 1 goto :build_fail
echo.
echo OK - build gerada em "%OUT%"
echo Executavel: "%OUT%\FURY Launcher.exe"
echo ^(self-contained: roda sem precisar do .NET instalado^)
pause
goto :end

rem ================== rotinas ==================
:ensure_dotnet
where dotnet >nul 2>nul
if not errorlevel 1 (
  echo .NET SDK encontrado.
  goto :eof
)
echo [DEP] .NET SDK nao encontrado no PATH.
where winget >nul 2>nul
if errorlevel 1 (
  echo [ERRO] winget indisponivel para instalar automaticamente.
  echo        Instale o .NET 8 SDK manualmente:
  echo        https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
echo Instalando .NET 8 SDK via winget ^(pode pedir permissao do Windows^)...
winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
rem winget nao atualiza o PATH desta janela; injeta o caminho padrao do dotnet:
set "PATH=%ProgramFiles%\dotnet;%PATH%"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo.
  echo [ATENCAO] .NET foi instalado, mas esta janela ainda nao enxerga o 'dotnet'.
  echo           Feche esta janela e execute o run.bat novamente.
  pause
  exit /b 1
)
echo .NET SDK instalado com sucesso.
goto :eof

:build_fail
echo.
echo [ERRO] Falha na etapa de restore/build. Veja as mensagens acima.
pause
goto :end

:end
endlocal
