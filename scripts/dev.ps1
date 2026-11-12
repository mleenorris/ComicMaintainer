<#
.SYNOPSIS
  Sets up a ComicMaintainer development machine and runs the CI checks locally.

.DESCRIPTION
  setup  Installs the tools CI uses, at the same versions, under -ToolsDir (default %USERPROFILE%\comicmaintainer-tools)
         without administrator rights: the .NET 11 SDK (unless one is already installed), Node.js, the CodeQL CLI and
         Trivy. It then restores the repository's .NET tools and npm packages. With -Android it also installs the
         Android toolchain through scripts\android-local.ps1, which accepts the Android SDK licences.

  ci     Runs the same checks as the GitHub workflows and prints a summary. Choose steps with -Only, for example
         -Only codeql or -Only build,unit. The steps, in order:
           build        strict Release build of Core, WebApi and the tests     (test-dotnet.yml)
           unit         unit tests and the line coverage gate                 (test-dotnet.yml)
           integration  integration tests                                     (test-dotnet.yml)
           js           JavaScript tests and their coverage gates             (test-dotnet.yml)
           nuget        NuGet packages with high or critical advisories       (security-scan.yml)
           codeql       CodeQL security-and-quality: C#, JavaScript, Actions  (codeql-analysis.yml)
           docker       image build, container smoke test and Trivy scan      (integration-tests-e2e.yml)
         Results, including the CodeQL SARIF files, go to <ToolsDir>\ci-results\<checkout folder name>.

  env    Puts the tools on PATH in the current PowerShell session. Dot-source it:  . .\scripts\dev.ps1 env

.NOTES
  CI also runs a file-operation job on macOS, which cannot run here. CI measures coverage on Linux, which reads about
  one point higher than Windows, so the unit step's gate is 96.3% on Windows and 97% elsewhere (-CoverageGate to
  change it). The docker step needs Docker and is skipped with a message when Docker is not installed.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\dev.ps1 setup
  powershell -ExecutionPolicy Bypass -File scripts\dev.ps1 ci
  powershell -ExecutionPolicy Bypass -File scripts\dev.ps1 ci -Only codeql
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('setup', 'ci', 'env')]
    [string]$Command = 'ci',
    # build, unit, integration, js, nuget, codeql or docker; several separated by commas.
    [string[]]$Only,
    [string]$ToolsDir = (Join-Path ([Environment]::GetFolderPath('UserProfile')) 'comicmaintainer-tools'),
    [switch]$Android,
    [double]$CoverageGate = 0,
    # Memory for CodeQL's query evaluation, in MB. 4096 is enough for every language, even on an 8 GB machine. Left to
    # itself (0), CodeQL sizes it from the free memory when it starts, which can be too little for the C# queries.
    [int]$CodeQLRamMb = 4096,
    # The CodeQL languages to scan: csharp, javascript and actions by default; several separated by commas.
    [string[]]$CodeQLLanguage = @('csharp', 'javascript', 'actions')
)

# "powershell -File" passes "-Only build,unit" as one string rather than a list, so split lists here and check them.
function Split-List([string[]]$Values, [string[]]$Allowed, [string]$Name) {
    $items = @($Values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().ToLowerInvariant() } | Where-Object { $_ })
    $unknown = @($items | Where-Object { $Allowed -notcontains $_ })
    if ($unknown.Count -gt 0) { throw "Unknown $Name '$($unknown -join ', ')'. Use: $($Allowed -join ', ')." }
    return $items
}
$Only = Split-List $Only @('build', 'unit', 'integration', 'js', 'nuget', 'codeql', 'docker') '-Only step'
$CodeQLLanguage = Split-List $CodeQLLanguage @('csharp', 'javascript', 'actions') '-CodeQLLanguage'

# The versions CI uses. Update these when the workflows move on.
$DotnetChannel = '11.0'    # actions/setup-dotnet with dotnet-version 11.0.x
$NodeMajor = 24            # actions/setup-node with node-version lts/*
$CodeQLVersion = '2.27.4'  # the bundle github/codeql-action v4 downloads
$TrivyVersion = '0.70.0'   # the Trivy that aquasecurity/trivy-action v0.36.0 installs

# Dot-sourcing env must not change the caller's error handling.
if ($Command -ne 'env') { $ErrorActionPreference = 'Stop' }
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo = Split-Path -Parent $PSScriptRoot
$Downloads = Join-Path $ToolsDir 'downloads'
# One results folder per checkout, so checks running in two worktrees at once do not clear each other's results.
$ResultsDir = Join-Path (Join-Path $ToolsDir 'ci-results') (Split-Path -Leaf $Repo)
$IsWindowsHost = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$ExeSuffix = if ($IsWindowsHost) { '.exe' } else { '' }
# Windows' own tar unpacks zip files too; a GNU tar earlier on PATH would not.
$Tar = if ($IsWindowsHost) { Join-Path $env:SystemRoot 'System32\tar.exe' } else { 'tar' }
$Projects = @(
    'src\ComicMaintainer.Core\ComicMaintainer.Core.csproj',
    'src\ComicMaintainer.WebApi\ComicMaintainer.WebApi.csproj',
    'tests\ComicMaintainer.Tests\ComicMaintainer.Tests.csproj')

function Write-Step([string]$Text) { Write-Host "`n== $Text" -ForegroundColor Cyan }

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

function Assert-Sha256([string]$File, [string]$Expected) {
    $actual = (Get-FileHash -Algorithm SHA256 -Path $File).Hash
    if ($actual -ne $Expected.Trim().ToUpperInvariant()) { throw "Checksum mismatch for $File." }
}

# Unpacks an archive (zip or tar.gz, with Windows' built-in tar) whose content is one top-level folder, and moves that
# folder to $Destination.
function Expand-Single([string]$Archive, [string]$Destination) {
    $unpack = "$Destination.unpack"
    if (Test-Path $unpack) { Remove-Item -Recurse -Force $unpack }
    New-Item -ItemType Directory -Force -Path $unpack | Out-Null
    & $Tar -xf $Archive -C $unpack
    if ($LASTEXITCODE -ne 0) { throw "Could not unpack $Archive." }
    $inner = @(Get-ChildItem -Path $unpack -Directory)
    if ($inner.Count -ne 1) { throw "Expected one folder in $Archive." }
    if (Test-Path $Destination) { Remove-Item -Recurse -Force $Destination }
    Move-Item -Path $inner[0].FullName -Destination $Destination
    Remove-Item -Recurse -Force $unpack
}

# Runs a program with its output on the console, and fails if it fails.
function Invoke-Tool([string]$What, [string]$Program, [string[]]$Arguments) {
    & $Program @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

function Find-Dotnet {
    $private = Join-Path $ToolsDir "dotnet\dotnet$ExeSuffix"
    if (Test-Path $private) { return $private }
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath -and ((& $onPath.Source --list-sdks) -match "^$([regex]::Escape($DotnetChannel))\.")) { return $onPath.Source }
    return $null
}

function Find-Node {
    $private = Join-Path $ToolsDir "node\node$ExeSuffix"
    if (Test-Path $private) { return $private }
    $onPath = Get-Command node -ErrorAction SilentlyContinue
    if ($onPath -and ((& $onPath.Source --version) -match "^v(\d+)\.") -and ([int]$Matches[1] -ge $NodeMajor)) { return $onPath.Source }
    return $null
}

function Find-Tool([string]$Name) {
    $private = Join-Path $ToolsDir "$Name\$Name$ExeSuffix"
    if (Test-Path $private) { return $private }
    $onPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

# Puts the tools first on PATH for this process (and, when dot-sourced, the caller's session).
function Use-Tools {
    $dirs = @()
    $dotnet = Find-Dotnet
    if ($dotnet) {
        $dotnetDir = Split-Path -Parent $dotnet
        $dirs += $dotnetDir
        if ($dotnetDir -like "$ToolsDir*") { $env:DOTNET_ROOT = $dotnetDir; $env:DOTNET_MULTILEVEL_LOOKUP = '0' }
    }
    foreach ($tool in @((Find-Node), (Find-Tool 'codeql'), (Find-Tool 'trivy'))) {
        if ($tool) { $dirs += (Split-Path -Parent $tool) }
    }
    $separator = [IO.Path]::PathSeparator
    $env:PATH = (($dirs | Select-Object -Unique) + @($env:PATH)) -join $separator
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
}

function Install-Tools {
    if (-not $IsWindowsHost) {
        throw "setup installs the Windows builds of the tools. On Linux or macOS install the .NET $DotnetChannel SDK, Node.js $NodeMajor, CodeQL $CodeQLVersion and Trivy $TrivyVersion yourself; ci then finds them on PATH."
    }
    New-Item -ItemType Directory -Force -Path $ToolsDir | Out-Null

    Write-Step ".NET $DotnetChannel SDK"
    if (Find-Dotnet) {
        Write-Host "Using $(Find-Dotnet)"
    }
    else {
        $installer = Get-File 'https://dot.net/v1/dotnet-install.ps1' 'dotnet-install.ps1'
        & powershell -NoProfile -ExecutionPolicy Bypass -File $installer -Channel $DotnetChannel -InstallDir (Join-Path $ToolsDir 'dotnet')
        if ($LASTEXITCODE -ne 0) { throw 'dotnet-install failed.' }
    }

    Write-Step "Node.js $NodeMajor"
    if (Find-Node) {
        Write-Host "Using $(Find-Node)"
    }
    else {
        $release = (Invoke-RestMethod -Uri 'https://nodejs.org/dist/index.json' -UseBasicParsing) |
            Where-Object { $_.version -like "v$NodeMajor.*" } | Select-Object -First 1
        $name = "node-$($release.version)-win-x64.zip"
        $zip = Get-File "https://nodejs.org/dist/$($release.version)/$name" $name
        $sums = (Invoke-WebRequest -Uri "https://nodejs.org/dist/$($release.version)/SHASUMS256.txt" -UseBasicParsing).Content
        $line = ($sums -split "`n") | Where-Object { $_ -match "\s$([regex]::Escape($name))$" } | Select-Object -First 1
        if (-not $line) { throw "No checksum published for $name." }
        Assert-Sha256 $zip ($line -split '\s+')[0]
        Expand-Single $zip (Join-Path $ToolsDir 'node')
    }

    Write-Step "CodeQL $CodeQLVersion"
    $codeql = Join-Path $ToolsDir 'codeql\codeql.exe'
    if ((Test-Path $codeql) -and ((& $codeql version --format=terse) -eq $CodeQLVersion)) {
        Write-Host "Using $codeql"
    }
    else {
        $base = "https://github.com/github/codeql-action/releases/download/codeql-bundle-v$CodeQLVersion"
        $bundle = Get-File "$base/codeql-bundle-win64.tar.gz" 'codeql-bundle-win64.tar.gz'
        $checksum = Get-File "$base/codeql-bundle-win64.tar.gz.checksum.txt" 'codeql-bundle-win64.tar.gz.checksum.txt'
        Assert-Sha256 $bundle ((Get-Content $checksum -Raw).Trim() -split '\s+')[0]
        Expand-Single $bundle (Join-Path $ToolsDir 'codeql')
    }

    Write-Step "Trivy $TrivyVersion"
    $trivyDir = Join-Path $ToolsDir 'trivy'
    $trivy = Join-Path $trivyDir 'trivy.exe'
    if ((Test-Path $trivy) -and ((& $trivy --version | Out-String) -match [regex]::Escape($TrivyVersion))) {
        Write-Host "Using $trivy"
    }
    else {
        $base = "https://github.com/aquasecurity/trivy/releases/download/v$TrivyVersion"
        $name = "trivy_${TrivyVersion}_windows-64bit.zip"
        $zip = Get-File "$base/$name" $name
        $sums = Get-Content (Get-File "$base/trivy_${TrivyVersion}_checksums.txt" "trivy_${TrivyVersion}_checksums.txt")
        $line = $sums | Where-Object { $_ -match "\s$([regex]::Escape($name))$" } | Select-Object -First 1
        if (-not $line) { throw "No checksum published for $name." }
        Assert-Sha256 $zip ($line -split '\s+')[0]
        if (Test-Path $trivyDir) { Remove-Item -Recurse -Force $trivyDir }
        New-Item -ItemType Directory -Force -Path $trivyDir | Out-Null
        & $Tar -xf $zip -C $trivyDir
        if ($LASTEXITCODE -ne 0) { throw "Could not unpack $zip." }
    }

    Use-Tools
    Write-Step 'Repository tools and packages'
    Push-Location $Repo
    try {
        Invoke-Tool 'dotnet tool restore' 'dotnet' @('tool', 'restore')
        Invoke-Tool 'npm ci' 'npm' @('ci', '--no-audit', '--no-fund')
    }
    finally { Pop-Location }

    if ($Android) {
        Write-Step 'Android toolchain'
        & (Join-Path $PSScriptRoot 'android-local.ps1') setup
    }

    Write-Step 'Docker'
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        Write-Host 'Docker is installed, so ci can run the docker step.'
    }
    else {
        Write-Host 'Docker is not installed, so ci skips the docker step. Docker Desktop needs WSL 2, which needs CPU virtualization turned on in Windows.'
    }
    Write-Host "`nReady. Run the checks with: powershell -ExecutionPolicy Bypass -File scripts\dev.ps1 ci"
}

function Get-CoveragePercent([string]$Directory) {
    $file = Get-ChildItem -Path $Directory -Recurse -Filter 'coverage.cobertura.xml' | Select-Object -First 1
    if (-not $file) { throw "No coverage.cobertura.xml under $Directory." }
    $reader = [Xml.XmlReader]::Create($file.FullName)
    try {
        [void]$reader.MoveToContent()
        return 100.0 * [double]$reader.GetAttribute('lines-covered') / [double]$reader.GetAttribute('lines-valid')
    }
    finally { $reader.Dispose() }
}

# Reads a CodeQL SARIF file, drops results in generated code under obj/ as the workflow does, and returns one object
# per result.
function Read-Sarif([string]$Path) {
    $sarif = Get-Content -Path $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    $findings = @()
    foreach ($run in $sarif.runs) {
        $rules = @{}
        $components = @($run.tool.driver) + @($run.tool.extensions)
        foreach ($component in $components) {
            foreach ($rule in @($component.rules)) { if ($rule -and $rule.id) { $rules[$rule.id] = $rule } }
        }
        foreach ($result in @($run.results)) {
            if (-not $result -or -not $result.locations) { continue }
            $location = $result.locations[0].physicalLocation
            $uri = $location.artifactLocation.uri
            if ($uri -match '(^|/)obj/') { continue }
            $rule = $rules[$result.ruleId]
            $level = $result.level
            if (-not $level -and $rule) { $level = $rule.defaultConfiguration.level }
            if (-not $level) { $level = 'warning' }
            $security = $null
            if ($rule -and $rule.properties) { $security = $rule.properties.'security-severity' }
            $findings += [pscustomobject]@{
                Rule = $result.ruleId
                Level = $level
                Security = if ($security) { [double]$security } else { $null }
                Location = "${uri}:$($location.region.startLine)"
                Message = ($result.message.text -replace '\s+', ' ')
            }
        }
    }
    return $findings
}

function Invoke-CodeQL {
    $codeql = Find-Tool 'codeql'
    if (-not $codeql) { throw 'CodeQL is not installed. Run: scripts\dev.ps1 setup' }
    $config = Join-Path $ResultsDir 'codeql-config.yml'
    # The same configuration as codeql-analysis.yml.
    [IO.File]::WriteAllLines($config, [string[]]@(
        'paths-ignore:', "  - '**/node_modules'", "  - '**/wwwroot/lib'", "  - '**/bin'", "  - '**/obj'",
        "  - '**/coveragereport'", "  - '**/*.min.js'", "  - '**/*.min.css'",
        # queries: +security-and-quality in the workflow adds this suite to the default one.
        'queries:', '  - uses: security-and-quality',
        'query-filters:', '  - exclude:', '      id: cs/unmanaged-code', '  - exclude:', '      id: cs/call-to-unmanaged-code'))
    # A traced, non-incremental build: CodeQL extracts C# by watching the compiler, so a build that compiles nothing
    # would leave the database empty.
    $buildScript = Join-Path $ResultsDir 'codeql-build.cmd'
    $dotnet = Find-Dotnet
    $lines = @('@echo off')
    foreach ($project in $Projects) {
        $lines += "`"$dotnet`" build `"$(Join-Path $Repo $project)`" --configuration Release --no-incremental -p:UseSharedCompilation=false || exit /b 1"
    }
    [IO.File]::WriteAllLines($buildScript, [string[]]$lines)

    $all = @()
    foreach ($language in $CodeQLLanguage) {
        Write-Step "CodeQL: $language"
        $database = Join-Path $ResultsDir "codeql-db-$language"
        $create = @('database', 'create', $database, "--language=$language", "--source-root=$Repo", '--overwrite',
            "--codescanning-config=$config")
        if ($language -eq 'csharp') { $create += "--command=$buildScript" }
        Invoke-Tool "CodeQL database for $language" $codeql $create
        $sarif = Join-Path $ResultsDir "codeql-$language.sarif"
        # No queries named here: the analysis takes them, and the query filters, from the configuration the database
        # was created with, as the workflow's analyze step does.
        $analyze = @('database', 'analyze', $database, '--format=sarif-latest', "--output=$sarif", '--threads=0')
        if ($CodeQLRamMb -gt 0) { $analyze += "--ram=$CodeQLRamMb" }
        Invoke-Tool "CodeQL analysis of $language" $codeql $analyze
        $all += @(Read-Sarif $sarif)
    }

    $report = Join-Path $ResultsDir 'codeql-findings.txt'
    $sorted = $all | Sort-Object @{ Expression = { if ($_.Security) { $_.Security } else { 0 } }; Descending = $true }, Level, Rule, Location
    [IO.File]::WriteAllLines($report, [string[]]@($sorted | ForEach-Object {
        $severity = if ($_.Security) { "security $($_.Security)" } else { $_.Level }
        "[$severity] $($_.Rule) $($_.Location): $($_.Message)"
    }))
    if ($all.Count -eq 0) { return 'no findings' }
    $all | Group-Object Rule | Sort-Object Count -Descending |
        Select-Object Count, Name, @{ n = 'Level'; e = { $_.Group[0].Level } }, @{ n = 'Security'; e = { $_.Group[0].Security } } |
        Format-Table -AutoSize | Out-Host
    $security = @($all | Where-Object { $_.Security }).Count
    throw "$($all.Count) findings, $security of them security findings. Details: $report"
}

function Invoke-Docker {
    if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { return 'SKIPPED: Docker is not installed.' }
    $image = 'comicmaintainer:ci'
    Invoke-Tool 'docker build' 'docker' @('build', '-f', (Join-Path $Repo 'Dockerfile.dotnet'), '-t', $image, $Repo)
    $bash = Get-Command bash -ErrorAction SilentlyContinue
    if (-not $bash -and $IsWindowsHost) { $bash = Get-Command 'C:\Program Files\Git\bin\bash.exe' -ErrorAction SilentlyContinue }
    if (-not $bash) { throw 'The container smoke test needs bash (Git for Windows provides it).' }
    $env:IMAGE = $image
    Invoke-Tool 'Container smoke test' $bash.Source @((Join-Path $Repo 'scripts/container-smoke-test.sh'))
    $trivy = Find-Tool 'trivy'
    if (-not $trivy) { throw 'Trivy is not installed. Run: scripts\dev.ps1 setup' }
    # Report, then fail only on fixable critical or high findings, as integration-tests-e2e.yml does.
    Invoke-Tool 'Trivy report' $trivy @('image', '--scanners', 'vuln', '--severity', 'CRITICAL,HIGH,MEDIUM', '--exit-code', '0', $image)
    Invoke-Tool 'Trivy gate' $trivy @('image', '--scanners', 'vuln', '--severity', 'CRITICAL,HIGH', '--ignore-unfixed', '--exit-code', '1', $image)
    return 'image built, smoke test passed, no fixable critical or high vulnerabilities'
}

function Invoke-Ci {
    Use-Tools
    if (-not (Find-Dotnet)) { throw "No .NET $DotnetChannel SDK found. Run: scripts\dev.ps1 setup" }
    if (Test-Path $ResultsDir) { Remove-Item -Recurse -Force $ResultsDir }
    New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
    $steps = @('build', 'unit', 'integration', 'js', 'nuget', 'codeql', 'docker')
    if ($Only) { $steps = $steps | Where-Object { $Only -contains $_ } }
    $gate = $CoverageGate
    if ($gate -le 0) { $gate = if ($IsWindowsHost) { 96.3 } else { 97 } }

    $checks = [ordered]@{
        build = {
            foreach ($project in $Projects) { Invoke-Tool "restore $project" 'dotnet' @('restore', $project) }
            foreach ($project in $Projects) {
                Invoke-Tool "build $project" 'dotnet' @('build', $project, '--no-restore', '--configuration', 'Release', '-p:StrictWarnings=true')
            }
            'no warnings or errors'
        }
        unit = {
            $directory = Join-Path $ResultsDir 'unit'
            Invoke-Tool 'unit tests' 'dotnet' @('test', $Projects[2], '--no-build', '--configuration', 'Release',
                '--filter', 'Category!=Integration', '--collect:XPlat Code Coverage', '--settings', 'coverlet.runsettings',
                '--results-directory', $directory, '--blame-hang-timeout', '5m')
            $percent = [math]::Round((Get-CoveragePercent $directory), 2)
            if ($percent -lt $gate) { throw "Line coverage $percent% is below the $gate% gate." }
            "line coverage $percent% (gate $gate%)"
        }
        integration = {
            Invoke-Tool 'integration tests' 'dotnet' @('test', $Projects[2], '--no-build', '--configuration', 'Release',
                '--filter', 'Category=Integration', '--blame-hang-timeout', '5m')
            'passed'
        }
        js = {
            Invoke-Tool 'npm ci' 'npm' @('ci', '--no-audit', '--no-fund')
            Invoke-Tool 'JavaScript tests' 'npm' @('run', 'test:coverage')
            'passed, coverage gates met'
        }
        nuget = {
            $reports = @()
            foreach ($project in $Projects[0..1]) {
                $name = [IO.Path]::GetFileNameWithoutExtension($project)
                Invoke-Tool "restore $name" 'dotnet' @('restore', $project)
                $json = & dotnet list $project package --vulnerable --include-transitive --format json
                if ($LASTEXITCODE -ne 0) { throw "dotnet list package failed for $name." }
                $report = Join-Path $ResultsDir "vulnerable-$name.json"
                [IO.File]::WriteAllLines($report, [string[]]@($json))
                $reports += $report
            }
            Invoke-Tool 'advisory check' 'node' (@('scripts/check-vulnerable-packages.js') + $reports)
            'no high or critical advisories'
        }
        codeql = { Invoke-CodeQL }
        docker = { Invoke-Docker }
    }

    $results = @()
    Push-Location $Repo
    try {
        foreach ($step in $steps) {
            Write-Step "ci: $step"
            $started = Get-Date
            try {
                $detail = (& $checks[$step] | Out-String).Trim()
                $status = if ($detail -like 'SKIPPED*') { 'skipped' } else { 'passed' }
            }
            catch {
                $status = 'FAILED'
                $detail = $_.Exception.Message
                Write-Host $detail -ForegroundColor Red
            }
            $results += [pscustomobject]@{
                Step = $step; Result = $status; Minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1); Detail = $detail
            }
        }
    }
    finally { Pop-Location }

    Write-Step 'Summary'
    $results | Format-Table -AutoSize -Wrap | Out-Host
    Write-Host "Results: $ResultsDir"
    if ($results | Where-Object { $_.Result -eq 'FAILED' }) { exit 1 }
}

switch ($Command) {
    'setup' { Install-Tools }
    'env' { Use-Tools; Write-Host "Tools from $ToolsDir are first on PATH in this session." }
    'ci' { Invoke-Ci }
}
