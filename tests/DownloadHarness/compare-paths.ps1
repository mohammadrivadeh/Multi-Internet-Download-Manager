[CmdletBinding()]
param(
    [Parameter(Mandatory)] [uri] $Source,
    [Parameter(Mandatory)] [ValidatePattern('^[a-fA-F0-9]{64}$')] [string] $ExpectedSha256,
    [Parameter(Mandatory)] [ValidateCount(2, 16)] [string[]] $AdapterId,
    [Parameter(Mandatory)] [ValidateCount(2, 16)] [string[]] $SourceAddress,
    [ValidateRange(2, 128)] [int] $Workers = 8,
    [ValidateRange(1, 15)] [int] $Rounds = 3
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $Source.IsAbsoluteUri -or $Source.Scheme -notin @('http', 'https') -or $Source.UserInfo) {
    throw 'Use an absolute HTTP(S) URL without embedded credentials.'
}
if ($AdapterId.Count -ne $SourceAddress.Count -or $Workers -lt $AdapterId.Count) {
    throw 'Provide one source address per adapter and at least one worker per selected adapter.'
}

$ids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
for ($index = 0; $index -lt $AdapterId.Count; $index++) {
    $address = $null
    if ([string]::IsNullOrWhiteSpace($AdapterId[$index]) -or -not $ids.Add($AdapterId[$index]) -or
        -not [System.Net.IPAddress]::TryParse($SourceAddress[$index], [ref] $address)) {
        throw 'Provide a distinct adapter ID and valid source IP address for every path.'
    }
}

function Get-Median([double[]] $Values) {
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -ne 0) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}

$project = Join-Path $PSScriptRoot 'DownloadHarness.csproj'
$combinedIndex = $AdapterId.Count
$labels = @(1..$combinedIndex | ForEach-Object { "Adapter $_" }) + 'Combined'
$trials = [System.Collections.Generic.List[object]]::new()
$expectedInterfaces = @{}
$expectedBytes = $null
$directory = Join-Path ([IO.Path]::GetTempPath()) ('midm-compare-' + [Guid]::NewGuid().ToString('N'))
[void] [IO.Directory]::CreateDirectory($directory)
try {
    for ($round = 0; $round -lt $Rounds; $round++) {
        for ($offset = 0; $offset -lt $labels.Count; $offset++) {
            $case = ($round + $offset) % $labels.Count
            $label = $labels[$case]
            $selection = if ($case -eq $combinedIndex) { 0..($combinedIndex - 1) } else { @($case) }
            $selection = @($selection)
            $target = Join-Path $directory ("round-{0}-case-{1}.bin" -f ($round + 1), $case)
            $arguments = foreach ($index in $selection) {
                $AdapterId[$index]
                $SourceAddress[$index]
            }
            try {
                $stdout = & dotnet run --project $project -c Debug --no-build -- --json paths $Source.AbsoluteUri $target $Workers @arguments 2>$null
                if ($LASTEXITCODE -ne 0) {
                    throw "The download harness failed for $label in round $($round + 1). Build it in Debug and check its local diagnostics."
                }
                $receipt = ($stdout -join "`n") | ConvertFrom-Json
                if ($receipt.Sha256 -ne $ExpectedSha256 -or $receipt.Bytes -le 0 -or
                    ($null -ne $expectedBytes -and $receipt.Bytes -ne $expectedBytes) -or
                    $receipt.ElapsedSeconds -le 0 -or [double]::IsNaN($receipt.ElapsedSeconds) -or
                    [double]::IsInfinity($receipt.ElapsedSeconds)) {
                    throw "The fixture or timing could not be verified for $label in round $($round + 1)."
                }
                $expectedBytes = $receipt.Bytes

                $paths = @($receipt.Paths)
                $interfaces = @($paths | ForEach-Object InterfaceIndex | Sort-Object -Unique)
                $checkpointed = ($paths | Measure-Object CheckpointedBytes -Sum).Sum
                if ($paths.Count -ne $selection.Count -or $interfaces.Count -ne $selection.Count -or
                    $checkpointed -ne $receipt.Bytes -or
                    @($paths | Where-Object { $_.InterfaceIndex -le 0 -or $_.CheckpointedBytes -le 0 -or $_.State -ne 'Available' }).Count -ne 0) {
                    throw "Range contribution could not be verified on every selected interface for $label in round $($round + 1)."
                }
                if ($case -ne $combinedIndex) {
                    if ($expectedInterfaces.ContainsKey($case) -and $expectedInterfaces[$case] -ne $interfaces[0]) {
                        throw "The interface changed for $label between rounds."
                    }
                    $expectedInterfaces[$case] = $interfaces[0]
                }
                $trials.Add([pscustomobject] @{
                    Round = $round + 1
                    Case = $label
                    JobId = $receipt.JobId
                    Sha256 = $receipt.Sha256
                    ElapsedSeconds = $receipt.ElapsedSeconds
                    MegabitsPerSecond = 8 * $receipt.Bytes / $receipt.ElapsedSeconds / 1e6
                    Paths = @($paths | Select-Object Ordinal, InterfaceIndex, ReceivedBytes, CheckpointedBytes, State)
                })
            }
            finally {
                foreach ($file in @($target, "$target.part", "$target.part.sqlite", "$target.part.sqlite-wal",
                    "$target.part.sqlite-shm", "$target.part.sqlite-journal")) {
                    if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file }
                }
            }
        }
    }
}
finally {
    [IO.Directory]::Delete($directory)
}

$baselineIndices = @($expectedInterfaces.Values | Sort-Object -Unique)
if ($baselineIndices.Count -ne $combinedIndex) {
    throw 'The single-adapter trials did not exercise distinct interface indices.'
}
foreach ($trial in $trials | Where-Object Case -eq 'Combined') {
    $indices = @($trial.Paths | ForEach-Object InterfaceIndex | Sort-Object)
    if (Compare-Object $baselineIndices $indices) {
        throw "The combined trial did not use the same interfaces as the single-adapter trials in round $($trial.Round)."
    }
}

$summary = foreach ($label in $labels) {
    $samples = @($trials | Where-Object Case -eq $label)
    [pscustomobject] @{
        Case = $label
        MedianSeconds = Get-Median ($samples.ElapsedSeconds)
        MedianMegabitsPerSecond = Get-Median ($samples.MegabitsPerSecond)
    }
}
$fastest = $summary | Where-Object Case -ne 'Combined' | Sort-Object MedianMegabitsPerSecond -Descending | Select-Object -First 1
$combined = $summary | Where-Object Case -eq 'Combined'
[pscustomobject] @{
    Workers = $Workers
    Rounds = $Rounds
    FixtureBytes = $expectedBytes
    FastestBaseline = $fastest.Case
    CombinedSpeedup = $combined.MedianMegabitsPerSecond / $fastest.MedianMegabitsPerSecond
    Summary = @($summary)
    Trials = @($trials.ToArray())
} | ConvertTo-Json -Depth 7
