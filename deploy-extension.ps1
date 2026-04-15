param(
	[ValidateSet("Debug", "Release")]
	[string]$Configuration = "Release",

	[string[]]$DnSpyPath,

	[string]$ExtensionName = "AgentSmithersMCPServer",

	[switch]$Build,

	[switch]$StageOnly,

	[string]$ArtifactsRoot = (Join-Path $PSScriptRoot "mcp-build")
)

$ErrorActionPreference = "Stop"

$extensionAssemblyName = "Example1.Extension.x.dll"
$extensionOutputRoots = @{
	"net48"          = Join-Path $PSScriptRoot "Extensions\Examples\Example1.Extension\bin\$Configuration\net48"
	"net8.0-windows" = Join-Path $PSScriptRoot "Extensions\Examples\Example1.Extension\bin\$Configuration\net8.0-windows"
}

function Invoke-ExtensionBuild {
	$buildScript = Join-Path $PSScriptRoot "build-ewdk.cmd"
	& $buildScript all $Configuration
	if ($LASTEXITCODE) {
		throw "build-ewdk.cmd failed with exit code $LASTEXITCODE"
	}
}

function Copy-DirectoryContents {
	param(
		[Parameter(Mandatory = $true)]
		[string]$Source,

		[Parameter(Mandatory = $true)]
		[string]$Destination
	)

	New-Item -ItemType Directory -Path $Destination -Force | Out-Null
	Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
		Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force
	}
}

function Remove-PathTree {
	param(
		[Parameter(Mandatory = $true)]
		[string]$Path
	)

	if (-not (Test-Path -LiteralPath $Path)) {
		return
	}

	try {
		Remove-Item -LiteralPath $Path -Recurse -Force
	}
	catch {
		$runningDnSpy = @(Get-Process -Name "dnSpy*" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty ProcessName -Unique)
		$hint = if ($runningDnSpy.Count -gt 0) {
			"Close dnSpy before deploying the extension. Running process(es): " + ($runningDnSpy -join ", ")
		}
		else {
			"Ensure no process is locking files under '$Path' and retry."
		}

		throw "Failed to replace extension directory '$Path'. $hint Original error: $($_.Exception.Message)"
	}
}

function Resolve-DnSpyRoot {
	param(
		[Parameter(Mandatory = $true)]
		[string]$Path
	)

	$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
	if (Test-Path (Join-Path $resolvedPath "dnSpy.exe")) {
		return $resolvedPath
	}

	$isBinDir = (Split-Path $resolvedPath -Leaf) -ieq "bin"
	if ($isBinDir -and (Test-Path (Join-Path $resolvedPath "dnSpy.dll"))) {
		$parentPath = Split-Path $resolvedPath -Parent
		if (Test-Path (Join-Path $parentPath "dnSpy.exe")) {
			return $parentPath
		}
	}

	throw "Could not resolve a dnSpy root from '$Path'. Pass the dnSpy folder root or its 'bin' subfolder."
}

function Get-DnSpyFlavor {
	param(
		[Parameter(Mandatory = $true)]
		[string]$DnSpyRoot
	)

	if (Test-Path (Join-Path $DnSpyRoot "bin\dnSpy.dll")) {
		return "net8.0-windows"
	}

	foreach ($marker in @("dnSpy.exe.config", "dnSpy-x86.exe.config", "dnSpy.Console.exe.config")) {
		if (Test-Path (Join-Path $DnSpyRoot $marker)) {
			return "net48"
		}
	}

	throw "Unable to detect dnSpy flavor under '$DnSpyRoot'."
}

function Get-DnSpyExtensionBaseDirectory {
	param(
		[Parameter(Mandatory = $true)]
		[string]$DnSpyRoot,

		[Parameter(Mandatory = $true)]
		[string]$DnSpyFlavor
	)

	switch ($DnSpyFlavor) {
		"net8.0-windows" {
			return (Join-Path $DnSpyRoot "bin")
		}

		"net48" {
			return $DnSpyRoot
		}
	}

	throw "Unsupported dnSpy flavor '$DnSpyFlavor'."
}

function Stage-ExtensionOutput {
	param(
		[Parameter(Mandatory = $true)]
		[string]$TargetFramework
	)

	$sourceDir = $extensionOutputRoots[$TargetFramework]
	if (-not (Test-Path $sourceDir)) {
		throw "Missing build output: $sourceDir"
	}

	$extensionAssemblyPath = Join-Path $sourceDir $extensionAssemblyName
	if (-not (Test-Path $extensionAssemblyPath)) {
		throw "Missing extension assembly: $extensionAssemblyPath"
	}

	$stagedDir = Join-Path $ArtifactsRoot "$ExtensionName\$Configuration\$TargetFramework"
	if (Test-Path $stagedDir) {
		Remove-PathTree -Path $stagedDir
	}

	Copy-DirectoryContents -Source $sourceDir -Destination $stagedDir
	return $stagedDir
}

function Get-DefaultDnSpyTargets {
	$candidates = @(
		(Join-Path $env:USERPROFILE "Downloads\Compressed\dnSpy-net-win32"),
		(Join-Path $env:USERPROFILE "Downloads\Compressed\dnSpy-net-win64")
	)

	return $candidates | Where-Object { Test-Path $_ }
}

if ($Build) {
	Invoke-ExtensionBuild
}

$stagedPackages = @{}
foreach ($targetFramework in @("net48", "net8.0-windows")) {
	$stagedPackages[$targetFramework] = Stage-ExtensionOutput -TargetFramework $targetFramework
}

Write-Host "Staged extension packages:"
foreach ($targetFramework in @("net48", "net8.0-windows")) {
	Write-Host "  $targetFramework => $($stagedPackages[$targetFramework])"
}

if ($StageOnly) {
	return
}

if (-not $DnSpyPath -or $DnSpyPath.Count -eq 0) {
	$DnSpyPath = @(Get-DefaultDnSpyTargets)
}

if (-not $DnSpyPath -or $DnSpyPath.Count -eq 0) {
	Write-Host "No dnSpy target paths supplied and no default installs were found. Staged packages only."
	return
}

$installResults = New-Object System.Collections.Generic.List[object]
foreach ($targetPath in $DnSpyPath) {
	$dnSpyRoot = Resolve-DnSpyRoot -Path $targetPath
	$dnSpyFlavor = Get-DnSpyFlavor -DnSpyRoot $dnSpyRoot
	$packagePath = $stagedPackages[$dnSpyFlavor]
	$installBaseDir = Get-DnSpyExtensionBaseDirectory -DnSpyRoot $dnSpyRoot -DnSpyFlavor $dnSpyFlavor
	$installDir = Join-Path $installBaseDir "Extensions\$ExtensionName"
	$legacyInstallDir = Join-Path $dnSpyRoot "Extensions\$ExtensionName"

	if ((-not [System.StringComparer]::OrdinalIgnoreCase.Equals($legacyInstallDir, $installDir)) -and (Test-Path $legacyInstallDir)) {
		Remove-PathTree -Path $legacyInstallDir
	}

	if (Test-Path $installDir) {
		Remove-PathTree -Path $installDir
	}

	Copy-DirectoryContents -Source $packagePath -Destination $installDir
	$installResults.Add([PSCustomObject]@{
		Target     = $dnSpyRoot
		Flavor     = $dnSpyFlavor
		BaseDir    = $installBaseDir
		InstallDir = $installDir
	}) | Out-Null
}

Write-Host ""
Write-Host "Installed extension:"
$installResults | Format-Table -AutoSize | Out-String | Write-Host
