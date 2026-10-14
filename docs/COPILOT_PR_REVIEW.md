# Automatic Copilot Pull Request Reviews

Every pull request should receive a GitHub Copilot code review automatically.
There are two ways to achieve this; the ruleset is preferred, and the workflow
in this repository is the fallback.

## Option 1 (recommended): repository ruleset

No repository files are involved — this is configured in GitHub settings:

1. Go to **Settings → Rules → Rulesets → New ruleset → New branch ruleset**.
2. Target the default branch (`dotnet` in this repository — it does **not** use
   `main`/`master`), or target all branches to cover every pull request base.
3. Set **Enforcement status** to **Active**.
4. Under **Branch rules**, enable **Require a pull request before merging** and
   check **Request pull request review from Copilot**.

Copilot is then added as a reviewer on every pull request targeting the selected
branches, including Dependabot pull requests. Draft pull requests are reviewed
only once they are marked ready for review.

The same setting can be applied at the organization level if it should apply to
all repositories.

## Option 2 (fallback): `copilot-pr-review.yml` workflow

[`.github/workflows/copilot-pr-review.yml`](../.github/workflows/copilot-pr-review.yml)
requests the Copilot reviewer through the REST API:

- Triggered on `pull_request_target` with types `opened` and `ready_for_review`.
- Uses `pull-requests: write` permission with the built-in `GITHUB_TOKEN`.
- Skips draft pull requests.
- Calls `POST /repos/{owner}/{repo}/pulls/{number}/requested_reviewers` with
  `copilot-pull-request-reviewer[bot]`, treating failures (for example a `422`
  when Copilot is already requested) as non-fatal.

`pull_request_target` is used so that pull requests from forks still get a token
that can request reviewers. The workflow never checks out or executes pull
request code, so no untrusted code runs with that token.

This workflow still requires Copilot code review to be enabled for the
repository; if it is not, the step logs a message and the job succeeds.

## Verification

Open a throwaway pull request against `dotnet` and confirm that **Copilot**
appears in the **Reviewers** list and posts a review.
