[CmdletBinding()]
param(
    [string]$Go = 'go',
    [string]$SourceRepository,
    [string]$WintunArchive,
    [switch]$Test,
    [switch]$IncludeWintun
)

$ErrorActionPreference = 'Stop'
$revision = '2b333a3ad820996ba337588adfa8f7259a45baec'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$work = Join-Path $root ('artifacts/net-merge/build-' + [Guid]::NewGuid())
$output = Join-Path $root 'artifacts/net-merge/windows-amd64'
$captureOutput = Join-Path $root 'artifacts/net-merge/desktop-capture'
$goExecutable = (Get-Command $Go -CommandType Application).Source
if ($WintunArchive) { $WintunArchive = (Resolve-Path -LiteralPath $WintunArchive).Path }
$env:GOTOOLCHAIN = 'local'
$env:GOOS = 'windows'
$env:GOARCH = 'amd64'
$env:CGO_ENABLED = '0'

function Invoke-Native {
    param([string]$Program, [string[]]$Arguments)
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program exited with code $LASTEXITCODE." }
}

function Update-Source {
    param([string]$File, [string]$Original, [string]$Replacement)
    $content = [IO.File]::ReadAllText($File)
    if (!$content.Contains($Original)) { throw "The pinned source no longer matches the desktop adaptation: $File" }
    [IO.File]::WriteAllText($File, $content.Replace($Original, $Replacement))
}

function Update-SourceRegion {
    param([string]$File, [string]$Start, [string]$End, [string]$Replacement)
    $content = [IO.File]::ReadAllText($File)
    $first = $content.IndexOf($Start, [StringComparison]::Ordinal)
    if ($first -lt 0) { throw "Missing desktop adaptation start in $File" }
    $last = $content.IndexOf($End, $first + $Start.Length, [StringComparison]::Ordinal)
    if ($last -lt 0) { throw "Missing desktop adaptation end in $File" }
    [IO.File]::WriteAllText($File, $content.Substring(0, $first) + $Replacement + $content.Substring($last))
}

$goVersion = Invoke-Native $goExecutable @('version')
if ($goVersion -notmatch '^go version go1\.27\.1 windows/amd64$') {
    throw "Expected Go 1.27.1 for Windows amd64. Found: $goVersion"
}

New-Item -ItemType Directory -Force $work | Out-Null
if (!$SourceRepository) {
    $SourceRepository = Join-Path $work 'repository'
    Invoke-Native git @('clone', '--no-checkout', 'https://github.com/net-merge/net-merge.git', $SourceRepository)
}
$SourceRepository = (Resolve-Path -LiteralPath $SourceRepository).Path
$archive = Join-Path $work 'source.zip'
Invoke-Native git @('-C', $SourceRepository, 'archive', '--format=zip', "--output=$archive", $revision)
$source = Join-Path $work 'source'
Expand-Archive -LiteralPath $archive -DestinationPath $source
$diagnostics = Join-Path $source 'cmd/adapter-diagnostics'
New-Item -ItemType Directory $diagnostics | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'diagnostics_windows.go') -Destination $diagnostics

$staged = Join-Path $work 'windows-amd64'
New-Item -ItemType Directory $staged | Out-Null
Push-Location $source
try {
    if ($Test) { Invoke-Native $goExecutable @('test', '-mod=readonly', './...') }
    Invoke-Native $goExecutable @('build', '-mod=readonly', '-trimpath', '-buildvcs=false',
        '-ldflags', "-X main.version=$revision", '-o', (Join-Path $staged 'net-merge.exe'), './cmd/net-merge')
    Invoke-Native $goExecutable @('build', '-mod=readonly', '-trimpath', '-buildvcs=false',
        '-o', (Join-Path $staged 'adapter-diagnostics.exe'), './cmd/adapter-diagnostics')
    Copy-Item -LiteralPath 'LICENSE', 'THIRD-PARTY-NOTICES.md' -Destination $staged
    if ($IncludeWintun) {
        $capture = Join-Path $work 'desktop-capture'
        New-Item -ItemType Directory $capture | Out-Null
        if ($WintunArchive) {
            Update-Source (Join-Path $source 'scripts/fetch-wintun.ps1') 'Invoke-WebRequest "https://www.wintun.net/builds/wintun-$version.zip" -OutFile $zip -TimeoutSec 120' '$zip = $env:MIDM_WINTUN_ARCHIVE'
            $env:MIDM_WINTUN_ARCHIVE = $WintunArchive
        }
        & ./scripts/fetch-wintun.ps1 -OutDir $capture

        $hostSource = Join-Path $source 'cmd/desktop-capture'
        New-Item -ItemType Directory $hostSource | Out-Null
        Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'desktop_host_windows*.go' | Copy-Item -Destination $hostSource
        $tun = Join-Path $source 'internal/tun/tun.go'
        $tunWindows = Join-Path $source 'internal/tun/tun_windows.go'
        Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'route_*.go' | Copy-Item -Destination (Join-Path $source 'internal/tun')
        Update-Source $tun 'tunName = "net-merge"' 'tunName = "Multi-Internet Balancer"'
        Update-Source $tun "`tCleanupStale()" ''
        Update-Source $tunWindows "`tstartStrongHost(ifc.Index)" ''
        Update-Source $tunWindows "`tidx := strconv.Itoa(ifc.Index)" @'
	if _, err := captureAdapterAtIndex(ifc.Index); err != nil {
		return 0, err
	}
	idx := strconv.Itoa(ifc.Index)
'@
        Update-SourceRegion $tunWindows "`tfor _, dst := range routes4win {" "`tif e := run(`"netsh`", `"interface`", `"ipv6`", `"add`", `"address`"" ''
        Update-SourceRegion $tunWindows "`t`tfor _, p := range routes6 {" "`treturn ifc.Index, nil" @'
	}
	if err := addOwnedCaptureRoutes(ifc.Index); err != nil {
		return ifc.Index, err
	}

'@
        Update-SourceRegion $tunWindows 'func delRoutes(' 'func run(' @'
func delRoutes(_ int) {
	if err := RecoverCaptureRoutes(); err != nil {
		logx.Printf("capture route recovery: %v\n", err)
	}
}

'@
        Update-Source $tunWindows 'logx.Printf("warning: could not set IPv4 MTU: %v\n", e)' 'return ifc.Index, e'
        Update-Source $tunWindows 'logx.Printf("warning: IPv6 not captured: %v\n", e)' 'return ifc.Index, e'
        Update-Source $tunWindows 'logx.Printf("warning: could not set IPv6 MTU: %v\n", e)' 'return ifc.Index, e'
        Update-Source $tunWindows 'windows.GUIDFromString("{3f0d5a7e-9b1c-4e52-8a47-1c6b2d9e5f30}")' 'windows.GUIDFromString(captureGUID)'
        Update-Source $tunWindows 'wun.WintunTunnelType = "net-merge"' 'wun.WintunTunnelType = "Multi-Internet Balancer"'
        Update-Source $tunWindows 'cmd := exec.Command(name, args...)' @'
	system, err := windows.GetSystemDirectory()
	if err != nil {
		return err
	}
	cmd := exec.Command(filepath.Join(system, name+".exe"), args...)
'@
        if ($Test) { Invoke-Native $goExecutable @('test', '-mod=readonly', './internal/tun', './cmd/desktop-capture') }
        Invoke-Native $goExecutable @('build', '-mod=readonly', '-trimpath', '-buildvcs=false',
            '-ldflags', '-H windowsgui', '-o', (Join-Path $capture 'desktop-capture.exe'), './cmd/desktop-capture')
        New-Item -ItemType Directory -Force $captureOutput | Out-Null
        Get-ChildItem -LiteralPath $capture -File | Copy-Item -Destination $captureOutput -Force
    }
}
finally { Pop-Location }

New-Item -ItemType Directory -Force $output | Out-Null
Get-ChildItem -LiteralPath $staged -File | Copy-Item -Destination $output -Force
Write-Output $output
