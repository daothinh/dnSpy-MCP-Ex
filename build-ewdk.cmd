@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "EWDK_SETUP=H:\BuildEnv\SetupBuildEnv.cmd"
set "BUILD_TFM=all"
set "CONFIGURATION=Release"
set "LOCAL_DOTNET=%SCRIPT_DIR%.dotnet"
set "DOTNET_VERSION=8.0.419"
set "STAGE_ROOT=%SCRIPT_DIR%mcp-build"

if /I "%~1"=="/?" goto :usage
if /I "%~1"=="-h" goto :usage
if /I "%~1"=="--help" goto :usage

if not exist "%EWDK_SETUP%" (
	echo EWDK setup script not found:
	echo   %EWDK_SETUP%
	exit /b 1
)

pushd "%SCRIPT_DIR%"

if not exist "Libraries\ICSharpCode.TreeView\ICSharpCode.TreeView.csproj" (
	echo Missing git submodules. Run:
	echo   git submodule update --init --recursive
	popd
	exit /b 1
)

if not exist "Extensions\ILSpy.Decompiler\ICSharpCode.Decompiler\ICSharpCode.Decompiler\ICSharpCode.Decompiler.csproj" (
	echo Missing git submodules. Run:
	echo   git submodule update --init --recursive
	popd
	exit /b 1
)

if not exist "dnSpy\dnSpy.Images\dnSpy.Images.csproj" (
	echo Missing git submodules. Run:
	echo   git submodule update --init --recursive
	popd
	exit /b 1
)

set "COMPLUS_LoadFromRemoteSources=1"

where pwsh >nul 2>nul
if errorlevel 1 (
	set "PS_EXE=powershell.exe"
) else (
	set "PS_EXE=pwsh.exe"
)

if exist "%LOCAL_DOTNET%\dotnet.exe" (
	goto :configure_dotnet
)

where dotnet >nul 2>nul
if not errorlevel 1 (
	goto :configure_dotnet
)

echo dotnet SDK not found. Bootstrapping local SDK %DOTNET_VERSION% into:
echo   %LOCAL_DOTNET%
echo.
if not exist ".tools" mkdir ".tools"
"%PS_EXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -Command "$ProgressPreference='SilentlyContinue'; Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile '.tools\dotnet-install.ps1'; & '.\.tools\dotnet-install.ps1' -Version '%DOTNET_VERSION%' -InstallDir '%LOCAL_DOTNET%'"
if errorlevel 1 (
	popd
	exit /b %errorlevel%
)

:configure_dotnet
if exist "%LOCAL_DOTNET%\dotnet.exe" (
	set "DOTNET_ROOT=%LOCAL_DOTNET%"
	set "PATH=%DOTNET_ROOT%;%PATH%"
	set "DOTNET_MULTILEVEL_LOOKUP=0"
	if exist "%DOTNET_ROOT%\sdk\%DOTNET_VERSION%\Sdks" (
		set "MSBuildSDKsPath=%DOTNET_ROOT%\sdk\%DOTNET_VERSION%\Sdks"
		set "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_DIR=%MSBuildSDKsPath%"
		set "DOTNET_MSBUILD_SDK_RESOLVER_SDKS_VER=%DOTNET_VERSION%"
		set "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR=%DOTNET_ROOT%"
		set "USE_DOTNET_MSBUILD=1"
	)
)

call "%EWDK_SETUP%"
if errorlevel 1 (
	popd
	exit /b %errorlevel%
)

echo Cleaning old MCP packages:
echo   %STAGE_ROOT%
if exist "%STAGE_ROOT%" rd /s /q "%STAGE_ROOT%"
echo.

echo Cleaning previous Release outputs from solution...
if defined DOTNET_ROOT (
	"%DOTNET_ROOT%\dotnet.exe" msbuild "%SCRIPT_DIR%dnSpy.sln" -restore -t:Clean -p:Configuration=%CONFIGURATION% -m -v:m
) else (
	dotnet msbuild "%SCRIPT_DIR%dnSpy.sln" -restore -t:Clean -p:Configuration=%CONFIGURATION% -m -v:m
)
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" goto :done

echo.
echo Building dnSpy using EWDK
echo   Target: %BUILD_TFM%
echo   Configuration: %CONFIGURATION%
if defined DOTNET_ROOT echo   DOTNET_ROOT: %DOTNET_ROOT%
echo.

if defined DOTNET_ROOT (
	"%DOTNET_ROOT%\dotnet.exe" msbuild "%SCRIPT_DIR%dnSpy.sln" -restore -t:Build -p:Configuration=%CONFIGURATION% -m -v:m
) else (
	dotnet msbuild "%SCRIPT_DIR%dnSpy.sln" -restore -t:Build -p:Configuration=%CONFIGURATION% -m -v:m
)
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" goto :done

echo.
echo Staging MCP extension packages to:
echo   %STAGE_ROOT%
echo.
"%PS_EXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%deploy-extension.ps1" -Configuration "%CONFIGURATION%" -StageOnly -ArtifactsRoot "%STAGE_ROOT%"
set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" (
	echo Failed to stage MCP extension packages.
)

:done
popd
exit /b %EXIT_CODE%

:usage
echo Usage:
echo   build-ewdk.cmd
echo.
echo This script always does:
echo   1. Clean old Release outputs
echo   2. Build the whole dnSpy solution in Release
echo   3. Copy staged MCP extension packages to:
echo      %STAGE_ROOT%
echo   build-ewdk.cmd
echo.
echo Notes:
echo   - Uses EWDK from: %EWDK_SETUP%
echo   - No parameters are required.
exit /b 0
