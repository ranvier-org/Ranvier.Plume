param([Parameter(Mandatory)][string]$Directory)
$ErrorActionPreference = 'Stop'
$files = @(Get-ChildItem -LiteralPath $Directory -Filter '*.nupkg')
if ($files.Count -ne 4) { throw "Expected four packages in $Directory" }
foreach ($id in 'Ranvier.Plume', 'Ranvier.Plume.View', 'Ranvier.Plume.Traced', 'Ranvier.Plume.View.Traced') {
    $assembly = $id -replace '\.Traced$', ''
    $version = (dotnet msbuild "src/$assembly/$assembly.fsproj" -getProperty:Version).Trim()
    $path = Join-Path $Directory "$id.$version.nupkg"
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $path))
    try {
        $names = $archive.Entries.FullName
        $entry = $archive.GetEntry("$id.nuspec")
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        $meta = $manifest.package.metadata
        if ($meta.id -ne $id -or $meta.version -ne $version) { throw "Unexpected identity in $path" }
        foreach ($name in 'README.md','licenses/Ranvier.Plume/LICENSE') {
            if ($names -notcontains $name) { throw "$id missing $name" }
        }
        foreach ($tfm in 'net8.0','net10.0') {
            foreach ($ext in 'xml','dll') {
                if ($names -notcontains "lib/$tfm/$assembly.$ext") { throw "$id missing $tfm $ext" }
            }
        }
        $suffix = if ($id.EndsWith('.Traced')) { '.Traced' } else { '' }
        foreach ($group in $meta.dependencies.group) {
            $deps = @($group.dependency)
            if ($assembly -eq 'Ranvier.Plume.View') {
                $sibling = $deps | Where-Object id -eq "Ranvier.Plume$suffix"
                if ($sibling.version -ne "[$version]") { throw "$id must pin its matching state package" }
                $keyboard = 'Ranvier.Keyboard.Dom'
            } else {
                if ($deps.id -notcontains "Ranvier$suffix") { throw "$id missing core dependency" }
                if ($names -notcontains 'fable/Keymap.fs') { throw "$id missing Fable keymap source" }
                $keyboard = 'Ranvier.Keyboard'
            }
            if (($deps | Where-Object id -eq $keyboard).version -ne '0.2.0') { throw "$id has unexpected keyboard dependency" }
        }
        Write-Host "Verified $id $version"
    } finally { $archive.Dispose() }
}
