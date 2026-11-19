# Publishing Plume

Both projects declare their package version in their project files. Update the state and view together. The traced packages are built with `RanvierTrace=true`. Each view pins the matching state package at the exact release version.

1. Open a pull request to `main`. **Verify** runs the state and native view tests, checks the traced build, and inspects the four NuGet archives. **Docs** builds the guides, assembly reference, sitemap and agent-readable Markdown with strict link checking.
2. After the checks pass, merge the pull request. GitHub Pages deploys the documentation from `main`.
3. Open **Actions → Publish NuGet → Run workflow** on `main`. Supply the successful **Verify** push run ID for that exact commit. The workflow checks provenance and downloads that run’s packages.
4. Trusted publishing exchanges the GitHub identity for a short-lived NuGet key. The workflow uploads the packages and creates the matching GitHub release. Set the repository variable `NUGET_USER` if the NuGet account for the policy changes.
5. Verify each package version on NuGet after indexing. A retry can reuse the same successful run. To rebuild the docs locally, run `dotnet run --project docs/docs.fsproj -c Release -- build --strict` and inspect `docs/output`.
