# CodeQL Configuration

CodeQL scans the repository for security problems and code quality issues. The workflow is
`.github/workflows/codeql-analysis.yml`.

## When it runs

- Pushes to `dotnet`
- Pull requests into `dotnet`
- Weekly, on Wednesday at 3:00 UTC
- By hand, from the Actions tab

The job also checks that it is running for `dotnet`, as the branch or the pull request's target, so it does nothing
elsewhere.

## What it scans

The `analyze-dotnet` job runs once for each language: `csharp`, `javascript` and `actions` (the workflow files). It uses
the `security-and-quality` query suite.

For C#, the job sets up .NET 10 and builds `ComicMaintainer.Core`, `ComicMaintainer.WebApi` and `ComicMaintainer.Tests` in
Release. The Android app is left out, because it needs a workload.

These paths are excluded:

- `**/node_modules`
- `**/wwwroot/lib`
- `**/bin`, `**/obj`
- `**/coveragereport`
- `**/*.min.js`, `**/*.min.css`

`paths-ignore` has no effect on compiled languages, so the C# analysis also covers what the source generators write under
`obj/`, such as the regex and OpenAPI generators. Those results cannot be fixed in this repository. The job therefore runs
the analysis with `upload: never`, removes every result located in an `obj/` directory, and uploads the rest with
`github/codeql-action/upload-sarif` under the `/language:<language>` category.

## Results

Results appear in the repository's **Security** tab under **Code scanning**. Filter by branch, language or severity.

Each run also keeps a short summary as the artifact `codeql-summary-dotnet-<language>` for 90 days.

## Limits

The job times out after 60 minutes, and each analysis after 30.

See also [GITHUB_ACTIONS_DOTNET.md](GITHUB_ACTIONS_DOTNET.md#security-scans) for the other scans.
