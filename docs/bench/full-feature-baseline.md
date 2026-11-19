# Full-feature editor performance baseline

Use **Ranvier.Plume v0.2.0** as the performance baseline for focused native typing and scrolling. The control is the Release EditorShowcase from that tag, including its native textarea, clipboard handlers, accessible selection, and typed diagnostic. Keep all showcase features enabled. Use **v0.1.1** for historical comparisons across the introduction of native input. [Historical input measurements](native-input.md)

## Reproduce the comparison

Build the sample in a checkout of the baseline tag and in the candidate checkout. Run the comparison from the candidate repository on the same Windows machine. Use one profiling session at a time.

```powershell
dotnet build "<baseline-checkout>/samples/EditorShowcase" -c Release
dotnet build samples/EditorShowcase -c Release
./scripts/compare-editor.ps1 -Baseline "<baseline-checkout>/samples/EditorShowcase/bin/Release/net10.0/EditorShowcase.dll" -Candidate samples/EditorShowcase/bin/Release/net10.0/EditorShowcase.dll > comparison.json
```

The runner focuses a hidden window, disables tiered compilation, and alternates process order between blocks. Each fresh process runs five rounds. Each round warms up for 30 turns and measures 160. The typing workload checks the inserted character count. Compare the candidate-minus-baseline differences in whole-turn time within blocks, then repeat promising changes with `-Frames 320`. Whole-turn time includes input dispatch, the showcase callback and a nonblocking native window step. Preserve the native input and accessibility tests alongside any performance improvement.

## Release measurement

Measured here on Windows x64 with Release .NET 10 in six fresh processes per workload. The median whole turn was **1.933 ms for focused typing** and **1.289 ms for a three-line scroll**. These figures record the release build under the machine load at measurement time. Remeasure the baseline alongside each candidate. [Raw process timings](full-feature-baseline.json)
