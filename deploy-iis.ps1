#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Deploys OfficeAschiApi to local IIS.

.PARAMETER SiteName
    IIS site name (default: OfficeAschi)

.PARAMETER Port
    Port to bind the site on (default: 8080)

.PARAMETER PublishDir
    Override the publish output folder.

.EXAMPLE
    .\deploy-iis.ps1
    .\deploy-iis.ps1 -SiteName "MyApp" -Port 9090
#>
param(
    [string]$SiteName  = "OfficeAschi",
    [int]   $Port      = 8080,
    [string]$PublishDir = "",
    [switch]$Help
)

# Show help if -Help or no arguments provided
if ($Help -or ($PSBoundParameters.Count -eq 0 -and $args.Count -eq 0)) {
    Get-Help $MyInvocation.MyCommand.Path -Detailed
    exit 0
}

$ErrorActionPreference = "Stop"
$rootDir = $PSScriptRoot

# ── 1. Resolve publish output path ──────────────────────────────────────────
if (-not $PublishDir) {
    $PublishDir = Join-Path $rootDir "publish"
}

# ── 2. Ensure IIS features are enabled ──────────────────────────────────────
Write-Host "Checking IIS features..." -ForegroundColor Cyan
$requiredFeatures = @(
    "IIS-WebServerRole",
    "IIS-WebServer",
    "IIS-ASPNET45",
    "NetFx4Extended-ASPNET45"
)
foreach ($feature in $requiredFeatures) {
    $state = (Get-WindowsOptionalFeature -Online -FeatureName $feature -ErrorAction SilentlyContinue)
    if ($state -and $state.State -ne "Enabled") {
        Write-Host "  Enabling $feature ..." -ForegroundColor Yellow
        Enable-WindowsOptionalFeature -Online -FeatureName $feature -All -NoRestart | Out-Null
    }
}

# ── 3. Ensure ASP.NET Core Hosting Bundle module is present ─────────────────
$ancmPath = "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"
if (-not (Test-Path $ancmPath)) {
    Write-Warning @"
ASP.NET Core Hosting Bundle is NOT installed.
Download and install it from:
  https://dotnet.microsoft.com/download/dotnet/10.0
Then re-run this script.
"@
    exit 1
}

# ── 4. Build Angular client app ─────────────────────────────────────────────
Write-Host "`nBuilding Angular client app..." -ForegroundColor Cyan
Push-Location (Join-Path $rootDir "ClientApp")
try {
    if (-not (Test-Path "node_modules")) {
        Write-Host "  Running npm ci..." -ForegroundColor Yellow
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
    }

    # Stamp version into Angular config
    $shaShort = git rev-parse --short HEAD 2>$null
    $buildNumber = git rev-list --count HEAD 2>$null
    if ($shaShort -and $buildNumber) {
        $version = "$buildNumber.0.0-$shaShort"
    } else {
        $version = "0.0.0-local"
    }
    $configFile = Join-Path $rootDir "ClientApp/src/app/app.config.ts"
    (Get-Content $configFile) -replace 'APP_VERSION_PLACEHOLDER', $version | Set-Content $configFile
    Write-Host "  Stamped version: $version" -ForegroundColor Green

    npm run build -- --configuration production
    if ($LASTEXITCODE -ne 0) { throw "Angular build failed" }

    # Restore placeholder so the source file stays clean for next run
    (Get-Content $configFile) -replace [regex]::Escape($version), 'APP_VERSION_PLACEHOLDER' | Set-Content $configFile
} finally {
    Pop-Location
}

# ── 5. Publish .NET app ─────────────────────────────────────────────────────
Write-Host "`nPublishing .NET app to $PublishDir ..." -ForegroundColor Cyan
dotnet publish (Join-Path $rootDir "OfficeAschiApi.csproj") `
    -c Release `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# ── 6. Set up IIS Application Pool ──────────────────────────────────────────
# Try IISAdministration first (modern), fall back to WebAdministration (legacy)
$useIISAdmin = $false
if (Get-Module -ListAvailable -Name IISAdministration) {
    Import-Module IISAdministration -ErrorAction Stop
    $useIISAdmin = $true
} elseif (Get-Module -ListAvailable -Name WebAdministration) {
    Import-Module WebAdministration -ErrorAction Stop
} else {
    throw "Neither IISAdministration nor WebAdministration PowerShell module is available. Install IIS Management Tools."
}

$appPoolName = $SiteName

if ($useIISAdmin) {
    # ── IISAdministration cmdlets ──
    $pool = Get-IISAppPool -Name $appPoolName -ErrorAction SilentlyContinue
    if (-not $pool) {
        Write-Host "Creating app pool '$appPoolName'..." -ForegroundColor Cyan
    }

    $mgr = Get-IISServerManager
    $pools = $mgr.ApplicationPools
    if (-not $pools[$appPoolName]) {
        $pools.Add($appPoolName) | Out-Null
    }
    $p = $pools[$appPoolName]
    $p.ManagedRuntimeVersion = ""
    $p.ProcessModel.IdentityType = [Microsoft.Web.Administration.ProcessModelIdentityType]::ApplicationPoolIdentity

    # ── 7. Create or update the IIS Site ──
    $sites = $mgr.Sites
    $site = $sites[$SiteName]
    if ($site) {
        Write-Host "Updating existing site '$SiteName'..." -ForegroundColor Yellow
        $site.Stop()
        $site.Applications["/"].VirtualDirectories["/"].PhysicalPath = $PublishDir
    } else {
        Write-Host "Creating IIS site '$SiteName' on port $Port..." -ForegroundColor Cyan
        $site = $sites.Add($SiteName, "http", "*:${Port}:", $PublishDir)
    }
    $site.ApplicationDefaults.ApplicationPoolName = $appPoolName
    $site.Applications["/"].ApplicationPoolName = $appPoolName
    $mgr.CommitChanges()

} else {
    # ── WebAdministration (IIS: drive) ──
    $appPoolPath = "IIS:\AppPools\$appPoolName"

    if (-not (Test-Path $appPoolPath)) {
        Write-Host "Creating app pool '$appPoolName'..." -ForegroundColor Cyan
        New-WebAppPool -Name $appPoolName | Out-Null
    }

    Set-ItemProperty $appPoolPath -Name managedRuntimeVersion -Value ""
    Set-ItemProperty $appPoolPath -Name processModel.identityType -Value "ApplicationPoolIdentity"

    # ── 7. Create or update the IIS Site ──
    $sitePath = "IIS:\Sites\$SiteName"

    if (Test-Path $sitePath) {
        Write-Host "Stopping existing site '$SiteName'..." -ForegroundColor Yellow
        Stop-Website -Name $SiteName -ErrorAction SilentlyContinue
        Set-ItemProperty $sitePath -Name physicalPath -Value $PublishDir
    } else {
        Write-Host "Creating IIS site '$SiteName' on port $Port..." -ForegroundColor Cyan
        New-Website -Name $SiteName `
            -PhysicalPath $PublishDir `
            -ApplicationPool $appPoolName `
            -Port $Port `
            -Force | Out-Null
    }

    Set-ItemProperty $sitePath -Name applicationPool -Value $appPoolName
}

# ── 8. Grant the app pool identity read access to the publish folder ────────
$acl = Get-Acl $PublishDir
$identity = "IIS AppPool\$appPoolName"
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    $identity, "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow"
)
$acl.SetAccessRule($rule)
Set-Acl $PublishDir $acl
Write-Host "Granted '$identity' read access to $PublishDir" -ForegroundColor Green

# ── 9. Ensure web.config exists (dotnet publish creates it) ─────────────────
$webConfig = Join-Path $PublishDir "web.config"
if (-not (Test-Path $webConfig)) {
    Write-Warning "web.config not found in publish output. Creating a default one..."
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*"
             modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet"
                  arguments=".\OfficeAschiApi.dll"
                  stdoutLogEnabled="false"
                  stdoutLogFile=".\logs\stdout"
                  hostingModel="InProcess" />
    </system.webServer>
  </location>
</configuration>
"@ | Set-Content $webConfig -Encoding UTF8
}

# ── 10. Start the site ──────────────────────────────────────────────────────
if ($useIISAdmin) {
    $mgr = Get-IISServerManager
    $mgr.Sites[$SiteName].Start()
} else {
    Start-Website -Name $SiteName
}
Write-Host "`n✅ Deployed successfully!" -ForegroundColor Green
Write-Host "   Site:  http://localhost:$Port" -ForegroundColor Green
Write-Host "   Pool:  $appPoolName (No Managed Code / InProcess)" -ForegroundColor Green
Write-Host "   Path:  $PublishDir" -ForegroundColor Green
