# Copilot CI Autofix

When an automated test workflow fails, Copilot CI Autofix gives the failure to the GitHub
Copilot cloud agent so it gets fixed without anyone having to start it, the same way the
application's automated error reports can be assigned to Copilot.

- Workflow: [`.github/workflows/copilot-ci-autofix.yml`](../.github/workflows/copilot-ci-autofix.yml)
- Logic: [`.github/scripts/copilot-ci-autofix.js`](../.github/scripts/copilot-ci-autofix.js),
  tested by [`tests/js/copilot-ci-autofix.test.js`](../tests/js/copilot-ci-autofix.test.js)
  (`npm test`)

## What it does

It reacts to the `.NET Tests` and `Integration Tests (E2E Container)` workflows:

| Run that completed | What happens |
| --- | --- |
| A failed run on a pull request from a branch of this repository | Comments `@copilot …` on the pull request. The comment lists the failed jobs and steps, includes log excerpts with the failing tests and their errors, and asks for a fix of the root cause without skipping or weakening tests. Copilot pushes the fix to the pull request's branch, and the new commit runs the tests again. |
| A failed run on `dotnet`, the default branch | Opens a `ci-failure` issue with the same details and assigns it to Copilot, which opens a pull request with the fix. |
| A passing run on `dotnet` | Closes the workflow's open `ci-failure` issue with a comment. |

Each run of the autofix workflow writes what it did, or why it did nothing, to its job
summary.

### Limits

- **Once per commit.** Copilot is asked at most once for a commit, even when several workflows
  fail on it. Failures that remain after its fix are reported for the new commit.
- **Three requests per pull request.** After that, a final note (which does not mention
  Copilot) says the limit was reached; to have Copilot try again, mention `@copilot` in a
  comment yourself. Change the limit with the `COPILOT_AUTOFIX_MAX_ATTEMPTS` repository
  variable.
- **Opt-out label.** Add the `no-copilot-autofix` label to a pull request to stop the
  requests on it.
- **One issue per workflow.** While a workflow's `ci-failure` issue is open, later failures on
  `dotnet` are added to it as comments instead of opening more issues. Copilot works from the
  issue description and does not read comments added after it was assigned, so these comments
  are for people.

### When nothing is requested

- **Copilot is still working on the branch.** The request waits up to 50 minutes for the
  running Copilot session to finish. If Copilot pushed more commits in the meantime, the run
  of the newest commit is handled instead. A session that is still running after 50 minutes
  is left alone.
- **The run is outdated.** A pull request run is ignored when the pull request has newer
  commits, and a `dotnet` run is ignored when a newer run of the workflow for a newer commit
  exists. The newer run is handled when it completes.
- **The failure comes from the base branch.** When every failed job of a pull request also
  fails in the latest push run of the workflow on its base branch, the pull request did not
  cause the failure and is not asked to fix it; on `dotnet` the `ci-failure` issue covers
  it. When only some jobs are failing on the base branch, the request marks them as
  probably not caused by the pull request. Pull requests that refer to an open `ci-failure`
  issue, as Copilot's pull requests for those issues do ("Fixes #12"), are asked anyway.
- **The pull request comes from a fork.** See [Security](#security).

## Setup

The workflow starts working once it is on the default branch (`dotnet`); `workflow_run`
workflows only run from the default branch.

1. **Check that Copilot cloud agent is available for the repository**: you can assign an
   issue to Copilot in the GitHub web interface.
2. **Create a token for a user with a Copilot license and write access**, normally the
   repository owner. The autofix needs a user's token because Copilot does not respond to
   comments written by bots or apps, and the workflow's `GITHUB_TOKEN` cannot assign issues
   to Copilot. Comments and issues are written as that user.
   - A fine-grained personal access token with access to this repository only and these
     repository permissions: **Actions**, **Contents**, **Issues** and **Pull requests**: read
     and write (**Metadata**: read is added automatically). Those are the permissions GitHub
     requires to assign issues to Copilot.
   - Or a classic personal access token with the `repo` scope.
3. **Store it as a repository secret** named `COPILOT_AUTOFIX_TOKEN` (**Settings → Secrets
   and variables → Actions → New repository secret**). Without it, failures only produce a
   warning in the autofix run.
4. *(Optional)* Add a repository **variable** named `COPILOT_AUTOFIX_MAX_ATTEMPTS` to change
   the limit of three requests per pull request.
5. *(Optional)* **Let the tests run on Copilot's commits without approval.** By default,
   workflows do not run on commits Copilot pushes until someone selects **Approve and run
   workflows** on the pull request, so a fix is not tested, and a failing fix is not sent
   back to Copilot, until then. To let the loop run unattended, turn off the approval
   requirement in the repository's Copilot cloud agent settings. Workflows then run code
   written by Copilot before anyone reviews it.

When the token expires or is revoked, the autofix runs fail with `Bad credentials`; store a
new token in the secret.

Each request and each issue starts a Copilot session, which uses premium requests.

## Security

The autofix workflow runs with access to the `COPILOT_AUTOFIX_TOKEN` secret, so:

- It never checks out or runs code from the failed run. It runs only the autofix script,
  checked out from the default branch.
- Its `GITHUB_TOKEN` is read-only (`actions: read`, `contents: read`) and is used only to
  read the failed jobs and their logs. Everything it writes is written with the user token.
- It ignores runs from forks. Their logs and pull requests are written by people without
  access to the repository, and Copilot would act on them with the token owner's access.
  Copilot is asked about pull requests from branches of this repository, including
  Dependabot's and Copilot's own pull requests.
- The log excerpts are added to comments and issues inside code blocks, and the run data
  is never inserted into the workflow's script.

## Watching other workflows

Add the workflow's `name:` (not its file name) to `workflows:` in
`copilot-ci-autofix.yml`. Pull request failures are handled for workflows that run on
`pull_request`, and issues are opened for workflows that run on `push` to `dotnet`.

`Integration Tests (E2E Container)` currently runs only for the `main` and `develop`
branches, which this repository does not use, so its failures are not handled until its
branch filters include `dotnet`.

## Checking that it works

1. Open a pull request with a test that fails.
2. When `.NET Tests` has finished, a **Copilot CI Autofix** run starts in the **Actions**
   tab. Its job summary says what it did.
3. The pull request gets the `@copilot` comment, and Copilot starts a session on the branch
   (shown on the pull request and in the **Agents** tab).

If the comment is posted but Copilot does not start, check that the token's owner has a
Copilot license and write access to the repository.
