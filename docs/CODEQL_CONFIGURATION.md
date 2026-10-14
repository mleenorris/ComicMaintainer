# CodeQL Configuration

This document explains how CodeQL security scanning is configured for the ComicMaintainer repository.

## Current setup: CodeQL default setup

CodeQL runs through GitHub's **default setup**, which is enabled in the repository settings
(*Settings → Code security → Code scanning → CodeQL analysis*). GitHub chooses the languages,
builds C# in `none` build mode and runs the analysis on pushes, pull requests and on a schedule.

There is intentionally **no CodeQL workflow file** in `.github/workflows`. A repository can only
use one of the two configurations at a time: while default setup is enabled, any advanced
workflow that uploads CodeQL results fails with

```
Code Scanning could not process the submitted SARIF file:
CodeQL analyses from advanced configurations cannot be processed when the default setup is enabled
```

The former `.github/workflows/codeql-analysis.yml` failed this way on every run (including every
Dependabot pull request), so it was removed in favour of default setup.

## Viewing results

1. Navigate to the repository's **Security** tab
2. Click **Code scanning alerts**
3. Filter by branch, tool or severity as needed

## Switching back to an advanced configuration

An advanced workflow is only needed for things default setup cannot express, such as a custom
query suite (`security-and-quality`), custom path filters, or a hand-written build for the
C# projects that skips the MAUI app. To switch:

1. In *Settings → Code security → Code scanning*, set **CodeQL analysis** to **Advanced**
   (or disable default setup). This step must happen first; otherwise the workflow's uploads
   are rejected.
2. Add a workflow that calls `github/codeql-action/init`, builds the projects and calls
   `github/codeql-action/analyze`.
3. Update this document to describe the workflow.

## Other security scanning

Dependency and container scanning are covered by `.github/workflows/security-scan.yml`; see
[SECURITY_SCANNING.md](SECURITY_SCANNING.md).
