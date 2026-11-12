<#
.SYNOPSIS
  Builds the Android app and checks it on an emulator on this PC, without Visual Studio or Android Studio.

.DESCRIPTION
  Steps (run one, or "all" for build, verify and emulator):
    setup     Installs a JDK, the Android SDK, the emulator and an x86_64 system image under -ToolchainDir,
              plus a .NET 11 SDK with the Android workload if none is found. This ACCEPTS THE ANDROID SDK
              LICENCES on your behalf: only run it if you agree to them.
    build     Publishes a signed Release APK, as the CI workflow does.
    verify    Checks the APK's signature and prints its package, version, minimum Android version,
              permissions and CPU architectures.
    emulator  Starts an emulator (creating it the first time), installs the APK, launches the app and
              fails if the app crashes or stops. The emulator is left running so you can use the app;
              a server on this PC is reachable from it at http://10.0.2.2:<port>.

  The emulator needs hardware acceleration. On Windows that is the Windows Hypervisor Platform feature:
  in an administrator PowerShell run
      Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform -All
  and restart Windows.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\android-local.ps1 setup
  powershell -ExecutionPolicy Bypass -File scripts\android-local.ps1 all
  powershell -ExecutionPolicy Bypass -File scripts\android-local.ps1 verify -Apk C:\Downloads\ComicMaintainer-android.apk
#>
param(
    [Parameter(Position = 0)]
    [ValidateSet('setup', 'build', 'verify', 'emulator', 'all')]
    [string]$Step = 'all',
    [string]$ToolchainDir = (Join-Path $env:USERPROFILE 'android-toolchain'),
    [string]$Apk,
    [int]$BootTimeoutSeconds = 900,
    [switch]$Headless
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Repo 'src\ComicMaintainer.MauiApp\ComicMaintainer.MauiApp.csproj'
$PackageId = 'com.comicmaintainer.app'
$Sdk = Join-Path $ToolchainDir 'android-sdk'
$Jdk = Join-Path $ToolchainDir 'jdk'
$ToolchainDotnet = Join-Path $ToolchainDir 'dotnet'
$Downloads = Join-Path $ToolchainDir 'downloads'
$BuildTools = '36.0.0'
$Image = 'system-images;android-35;default;x86_64'
$AvdName = 'ComicMaintainer_API35'

$env:JAVA_HOME = $Jdk
$env:ANDROID_HOME = $Sdk
$env:ANDROID_SDK_ROOT = $Sdk
$env:ANDROID_AVD_HOME = Join-Path $ToolchainDir 'avd'

function Write-Step([string]$Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }

function Assert-Exit([string]$What) {
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

function Get-File([string]$Url, [string]$Name) {
    New-Item -ItemType Directory -Force -Path $Downloads | Out-Null
    $target = Join-Path $Downloads $Name
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Invoke-WebRequest -Uri $Url -OutFile $target -UseBasicParsing
            return $target
        }
        catch {
            if ($attempt -eq 3) { throw "Could not download $Url : $($_.Exception.Message)" }
            Start-Sleep -Seconds (5 * $attempt)
        }
    }
}

# Unpacks a zip whose content is one top-level folder and moves that folder to $Destination.
function Expand-Single([string]$Zip, [string]$Destination) {
    $unpack = "$Destination.unpack"
    if (Test-Path $unpack) { Remove-Item -Recurse -Force $unpack }
    Expand-Archive -Path $Zip -DestinationPath $unpack
    $inner = @(Get-ChildItem -Path $unpack -Directory)
    if ($inner.Count -ne 1) { throw "Expected one folder in $Zip." }
    if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Move-Item -Path $inner[0].FullName -Destination $Destination
    Remove-Item -Recurse -Force $unpack
}

function Get-SdkTool([string]$RelativePath) {
    $path = Join-Path $Sdk $RelativePath
    if (-not (Test-Path $path)) { throw "$path not found. Run the setup step first." }
    return $path
}

function Get-Dotnet {
    $local = Join-Path $ToolchainDotnet 'dotnet.exe'
    if (Test-Path $local) {
        $env:DOTNET_ROOT = $ToolchainDotnet
        $env:DOTNET_MULTILEVEL_LOOKUP = '0'
        return $local
    }
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

function Install-Toolchain {
    Write-Step 'JDK 17'
    if (-not (Test-Path (Join-Path $Jdk 'bin\java.exe'))) {
        $zip = Get-File 'https://aka.ms/download-jdk/microsoft-jdk-17-windows-x64.zip' 'microsoft-jdk-17.zip'
        Expand-Single $zip $Jdk
    }
    & (Join-Path $Jdk 'bin\java.exe') -version
    Assert-Exit 'java -version'

    Write-Step 'Android command-line tools'
    $sdkManager = Join-Path $Sdk 'cmdline-tools\latest\bin\sdkmanager.bat'
    if (-not (Test-Path $sdkManager)) {
        $index = Get-File 'https://dl.google.com/android/repository/repository2-3.xml' 'repository2-3.xml'
        $match = Select-String -Path $index -Pattern 'commandlinetools-win-\d+_latest\.zip' | Select-Object -First 1
        if (-not $match) { throw 'Could not find the command-line tools in the Android repository index.' }
        $name = $match.Matches[0].Value
        $zip = Get-File "https://dl.google.com/android/repository/$name" $name
        Expand-Single $zip (Join-Path $Sdk 'cmdline-tools\latest')
    }

    Write-Step 'Android SDK packages (installing them accepts their licences on your behalf)'
    $answers = @('y') * 40
    $cli = Join-Path $Sdk 'cmdline-tools\latest\bin\android.exe'
    if (Test-Path $cli) {
        # Command-line tools 23 and later replace sdkmanager with the Android CLI, which writes package
        # paths with '/'. Its sdkmanager.bat wrapper splits arguments at ';', so it cannot be used here.
        $answers | & $cli "--sdk=$Sdk" sdk install 'platform-tools' "build-tools/$BuildTools" 'platforms/android-36' 'emulator' ($Image -replace ';', '/')
        Assert-Exit 'android sdk install'
    }
    else {
        $answers | & $sdkManager "--sdk_root=$Sdk" --licenses | Out-Null
        & $sdkManager "--sdk_root=$Sdk" 'platform-tools' "build-tools;$BuildTools" 'platforms;android-36' 'emulator' $Image
        Assert-Exit 'sdkmanager'
    }
    # Neither tool fails for a package it cannot find, so check that each one arrived.
    $required = @("build-tools\$BuildTools\aapt2.exe", 'platforms\android-36\android.jar', 'platform-tools\adb.exe',
        'emulator\emulator.exe', (Join-Path ($Image -replace ';', '\') 'system.img'))
    foreach ($file in $required) {
        if (-not (Test-Path (Join-Path $Sdk $file))) { throw "The Android SDK is missing $file after the install." }
    }

    Write-Step '.NET SDK with the Android workload'
    $dotnet = Get-Dotnet
    if (-not $dotnet) {
        $installer = Get-File 'https://dot.net/v1/dotnet-install.ps1' 'dotnet-install.ps1'
        & powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Channel 10.0 -InstallDir $ToolchainDotnet
        Assert-Exit 'dotnet-install'
        $dotnet = Get-Dotnet
    }
    $workloads = & $dotnet workload list | Out-String
    if ($workloads -notmatch 'maui-android') {
        & $dotnet workload install maui-android
        Assert-Exit 'dotnet workload install maui-android'
    }
    Write-Host "Toolchain ready in $ToolchainDir"
}

function Invoke-Build {
    Write-Step 'Publishing the Release APK'
    $dotnet = Get-Dotnet
    if (-not $dotnet) { throw 'No .NET SDK found. Run the setup step first.' }
    & $dotnet publish $Project -c Release -f net11.0-android -p:AndroidPackageFormat=apk "-p:AndroidSdkDirectory=$Sdk" "-p:JavaSdkDirectory=$Jdk"
    Assert-Exit 'dotnet publish'
    return Find-Apk
}

function Find-Apk {
    if ($Apk) { return (Resolve-Path $Apk).Path }
    $publish = Join-Path $Repo 'src\ComicMaintainer.MauiApp\bin\Release\net11.0-android\publish'
    $signed = Get-ChildItem -Path $publish -Filter '*-Signed.apk' -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $signed) { throw "No signed APK in $publish. Run the build step first, or pass -Apk." }
    return $signed.FullName
}

function Test-Apk([string]$Path) {
    Write-Step "Checking $Path"
    $apksigner = Get-SdkTool "build-tools\$BuildTools\apksigner.bat"
    $aapt2 = Get-SdkTool "build-tools\$BuildTools\aapt2.exe"
    # apksigner warns on stderr about every unsigned META-INF entry the AndroidX libraries ship; drop that noise.
    $certificates = cmd /c "`"$apksigner`" verify --print-certs `"$Path`" 2>nul"
    Assert-Exit 'apksigner verify'
    $certificates | Where-Object { $_ -match 'certificate (DN|SHA-256)' }
    if ($certificates -match 'CN=Android Debug') {
        Write-Warning 'Signed with the Android debug key, which differs per machine, so this APK cannot update one built elsewhere.'
    }
    $badging = & $aapt2 dump badging $Path
    Assert-Exit 'aapt2 dump badging'
    $badging | Where-Object { $_ -match "^(package|minSdkVersion|sdkVersion|targetSdkVersion|uses-permission|native-code|application-label):" }
    if (-not ($badging -match "^package: name='$([regex]::Escape($PackageId))'")) { throw "The APK's package is not $PackageId." }
    $sizeMb = [math]::Round((Get-Item $Path).Length / 1MB, 1)
    Write-Host "Signature verified. Size: $sizeMb MB."
}

# Writes the emulator's definition directly, so it always uses $Image. The Android CLI's
# "emulator create" picks a system image of its own, and avdmanager.bat splits arguments at ';'.
function New-Avd {
    Write-Step "Creating the $AvdName emulator"
    $avdDir = Join-Path $env:ANDROID_AVD_HOME "$AvdName.avd"
    New-Item -ItemType Directory -Force -Path $avdDir | Out-Null
    $parts = $Image -split ';'
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [IO.File]::WriteAllLines((Join-Path $env:ANDROID_AVD_HOME "$AvdName.ini"), [string[]]@(
        'avd.ini.encoding=UTF-8', "path=$avdDir", "path.rel=avd\$AvdName.avd", "target=$($parts[1])"), $utf8)
    [IO.File]::WriteAllLines((Join-Path $avdDir 'config.ini'), [string[]]@(
        "AvdId=$AvdName", "avd.ini.displayname=ComicMaintainer test phone", 'avd.ini.encoding=UTF-8',
        "abi.type=$($parts[3])", "hw.cpu.arch=$($parts[3])", "tag.id=$($parts[2])", "tag.display=$($parts[2])",
        "image.sysdir.1=$($parts -join '\')\", 'hw.ramSize=2048', 'vm.heapSize=256', 'hw.keyboard=yes',
        'hw.lcd.width=1080', 'hw.lcd.height=2400', 'hw.lcd.density=420', 'hw.gpu.enabled=yes', 'hw.gpu.mode=auto',
        'disk.dataPartition.size=4G', 'fastboot.forceColdBoot=yes'), $utf8)
}

function Wait-ForBoot([string]$Adb) {
    & $Adb wait-for-device
    $deadline = (Get-Date).AddSeconds($BootTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $booted = (& $Adb shell getprop sys.boot_completed) -join ''
        if ($booted.Trim() -eq '1') { return }
        Start-Sleep -Seconds 5
    }
    throw "The emulator did not finish booting within $BootTimeoutSeconds seconds."
}

function Test-OnEmulator([string]$Path) {
    $emulator = Get-SdkTool 'emulator\emulator.exe'
    $adb = Get-SdkTool 'platform-tools\adb.exe'
    $null = Get-SdkTool (Join-Path ($Image -replace ';', '\') 'system.img')

    Write-Step 'Hardware acceleration'
    & $emulator -accel-check
    if ($LASTEXITCODE -ne 0) {
        throw "The emulator has no hardware acceleration. In an administrator PowerShell run`n  Enable-WindowsOptionalFeature -Online -FeatureName HypervisorPlatform -All`nthen restart Windows and run this step again."
    }

    $running = (& $adb devices) -match '^emulator-\d+\s+device'
    if (-not $running) {
        $avdList = & $emulator -list-avds
        if (-not ($avdList -contains $AvdName)) {
            New-Avd
        }
        Write-Step "Starting the $AvdName emulator"
        $arguments = @('-avd', $AvdName, '-no-snapshot-save', '-no-boot-anim', '-memory', '2048')
        if ($Headless) { $arguments += '-no-window' }
        Start-Process -FilePath $emulator -ArgumentList $arguments | Out-Null
    }
    Wait-ForBoot $adb

    Write-Step 'Installing and launching the app'
    & $adb install -r $Path
    Assert-Exit 'adb install'
    & $adb logcat -c
    & $adb shell monkey -p $PackageId -c android.intent.category.LAUNCHER 1 | Out-Null
    Start-Sleep -Seconds 20

    $processId = ((& $adb shell pidof $PackageId) -join '').Trim()
    $crashes = (& $adb logcat -d -b crash) | Where-Object { $_ -match [regex]::Escape($PackageId) }
    if ($crashes) {
        $crashes | Select-Object -First 40
        throw 'The app crashed on launch. The lines above are from the crash log.'
    }
    if (-not $processId) { throw 'The app is not running 20 seconds after launch. Check "adb logcat" for the reason.' }
    $activity = (& $adb shell dumpsys activity activities) | Where-Object { $_ -match 'mResumedActivity|topResumedActivity' } | Select-Object -First 1
    Write-Host "The app is running (process $processId). $($activity.Trim())"
    Write-Host 'The emulator stays open. A server on this PC is at http://10.0.2.2:<port> from inside the emulator.'
}

switch ($Step) {
    'setup' { Install-Toolchain }
    'build' { $null = Invoke-Build; Write-Host "APK: $(Find-Apk)" }
    'verify' { Test-Apk (Find-Apk) }
    'emulator' { Test-OnEmulator (Find-Apk) }
    'all' {
        if (-not $Apk) { $null = Invoke-Build }
        $path = Find-Apk
        Test-Apk $path
        Test-OnEmulator $path
    }
}
