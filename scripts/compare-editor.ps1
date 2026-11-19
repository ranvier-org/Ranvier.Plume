[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Baseline,
    [string]$Candidate,
    [ValidateRange(2,100)][int]$Blocks = 6,
    [ValidateRange(1,10000)][int]$Frames = 160
)

$ErrorActionPreference = 'Stop'
$builds = [ordered]@{ baseline = (Resolve-Path -LiteralPath $Baseline).Path }
if ($Candidate) { $builds.candidate = (Resolve-Path -LiteralPath $Candidate).Path }
$prior = @{}
foreach ($key in 'DOTNET_TieredCompilation','PLUME_PROFILE_INPUT','PLUME_PROFILE') {
    $prior[$key] = [Environment]::GetEnvironmentVariable($key)
}
try {
    $env:DOTNET_TieredCompilation = '0'
    $env:PLUME_PROFILE_INPUT = '1'
    $samples = foreach ($block in 1..$Blocks) {
        $order = @($builds.Keys)
        if ($block % 2 -eq 0) { [array]::Reverse($order) }
        foreach ($workload in 'typing','lines x3') {
            $env:PLUME_PROFILE = "$workload|every feature"
            foreach ($build in $order) {
                $lines = & dotnet $builds[$build] --profile --frames $Frames
                if ($LASTEXITCODE) { throw "EditorShowcase failed for $build, $workload ($LASTEXITCODE)." }
                $pattern = '^\s*' + [regex]::Escape($workload) + '\s+every feature\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+'
                $row = @($lines | Select-String -Pattern $pattern)
                if ($row.Count -ne 1) { throw "Expected one summary for $build, $workload." }
                $g = $row[0].Matches[0].Groups
                [ordered]@{
                    block = $block; build = $build; workload = $workload
                    dispatchMs = [double]$g[1].Value; callbackMs = [double]$g[2].Value
                    windowStepMs = [double]$g[3].Value; wholeTurnMs = [double]$g[4].Value
                }
            }
        }
    }
    [ordered]@{ framesPerRound = $Frames; blocks = $Blocks; samples = @($samples) } | ConvertTo-Json -Depth 4
} finally {
    foreach ($key in $prior.Keys) { [Environment]::SetEnvironmentVariable($key, $prior[$key]) }
}
