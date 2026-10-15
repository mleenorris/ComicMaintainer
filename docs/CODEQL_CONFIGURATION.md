# CodeQL Configuration

The repository uses GitHub's **CodeQL default setup**. GitHub manages the
analysis workflow; no CodeQL analysis workflow is needed in `.github/workflows`.

## Managing CodeQL

Repository administrators can review the enabled languages, query suite, and
scan configuration under **Settings → Code security → CodeQL analysis**.
Manage those options in default setup rather than adding a custom workflow.

The former `.github/workflows/codeql-analysis.yml` used advanced setup and
duplicated the managed scans. Although analysis completed, GitHub rejected its
results with:

> CodeQL analyses from advanced configurations cannot be processed when the default setup is enabled

Do not reintroduce `github/codeql-action/init` or `github/codeql-action/analyze`
steps while default setup is enabled. If advanced setup is required in the
future, an administrator must first switch the repository away from default
setup and configure the replacement workflow.

## Other Security Scans

Dependency and container scanning remain in `.github/workflows/security-scan.yml`.
Its `github/codeql-action/upload-sarif` step uploads **Trivy** results, not CodeQL
analyses, and can coexist with CodeQL default setup.

## Viewing Results

1. Open the repository's **Security → Code scanning** page to view alerts.
2. Check the CodeQL tool status page for language coverage and analysis status.
3. In **Actions**, managed runs appear as **CodeQL** with names such as
   **Push on dotnet** or **PR #…**, rather than **CodeQL Advanced Security Scanning**.
