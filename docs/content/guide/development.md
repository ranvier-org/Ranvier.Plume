---
title: Development and measurement
description: Run the native showcase and reproduce the project checks.
---

# Development and measurement

From the checkout, run these commands in separate invocations.

```sh
dotnet run -c Release --project samples/EditorShowcase
dotnet fsi --exec build.fsx -- ci
dotnet fsi --exec build.fsx -- check
dotnet fsi --exec build.fsx -- traced
dotnet run --project docs/docs.fsproj -c Release -- build --strict
```

The first command opens the native editor. The CI target builds the solution and runs every state and view test. The check target also needs the global `rfs` tool. The traced target verifies the traced package family in a copy of the source tree.

For a hidden-window frame benchmark, append `-- --bench` to the showcase command. Use `--memory` for retained history, `--load` for the line scan, and `--writes` for row writes during typing and scrolling. Compare like workloads from the same probe. The [acceptance report](https://github.com/ranvier-org/Ranvier.Plume/blob/main/docs/ACCEPTANCE.md) describes the environment and links the results to the [design dossier](https://github.com/ranvier-org/Ranvier.Plume/blob/main/docs/DOSSIER.md).

For documentation clients, the site also publishes a [page index](/Ranvier.Plume/llms.txt) and [combined Markdown guide](/Ranvier.Plume/llms-full.txt).
