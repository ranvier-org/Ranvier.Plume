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
            foreach ($ranvierDep in ($deps | Where-Object { $_.id.StartsWith('Ranvier') })) {
                if ($suffix -eq '.Traced' -and -not $ranvierDep.id.EndsWith('.Traced')) {
                    throw "$id has untraced dependency $($ranvierDep.id)"
                }
                if ($suffix -eq '' -and $ranvierDep.id.EndsWith('.Traced')) {
                    throw "$id has traced dependency $($ranvierDep.id)"
                }
            }
            if ($assembly -eq 'Ranvier.Plume.View') {
                $sibling = $deps | Where-Object id -eq "Ranvier.Plume$suffix"
                if ($sibling.version -ne "[$version]") { throw "$id must pin its matching state package" }
                if (($deps | Where-Object id -eq "Ranvier.View$suffix").version -ne '0.4.0') { throw "$id has unexpected view dependency" }
                if (($deps | Where-Object id -eq "Ranvier.View.Dom$suffix").version -ne '0.4.0') { throw "$id has unexpected view dom dependency" }
                if (($deps | Where-Object id -eq "Ranvier.Css$suffix").version -ne '0.3.0') { throw "$id has unexpected css dependency" }
                if (($deps | Where-Object id -eq "Ranvier.Css.View$suffix").version -ne '0.3.1') { throw "$id has unexpected css view dependency" }
                $keyboard = "Ranvier.Keyboard.Dom$suffix"
            } else {
                if (($deps | Where-Object id -eq "Ranvier$suffix").version -ne '0.2.1') { throw "$id has unexpected core dependency" }
                if ($names -notcontains 'fable/Keymap.fs') { throw "$id missing Fable keymap source" }
                $keyboard = "Ranvier.Keyboard$suffix"
            }
            if (($deps | Where-Object id -eq $keyboard).version -ne '0.2.0') { throw "$id has unexpected keyboard dependency" }
        }
        Write-Host "Verified $id $version"
    } finally { $archive.Dispose() }
}
