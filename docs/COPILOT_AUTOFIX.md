# Copilot Autofix for CodeQL Alerts

Copilot fixes CodeQL alerts in two places:

| Where | Feature | What happens | Setup | Cost |
| --- | --- | --- | --- | --- |
| Pull requests | Copilot Autofix | Suggests a fix for each new alert in a pull request | None (on by default) | Free |
| Existing alerts on `dotnet` | Agentic Autofix | The [`codeql-agentic-autofix.yml`](../.github/workflows/codeql-agentic-autofix.yml) workflow assigns alerts to Copilot, which validates a fix and opens a draft pull request | [One setting and one secret](#setup) | Code Security AI credits and Actions minutes |

## Pull requests: Copilot Autofix

Copilot Autofix is on by default for repositories that use CodeQL. The
[CodeQL workflow](CODEQL_CONFIGURATION.md) runs on every pull request to `dotnet`,
and when it finds a new security alert in the diff, Copilot posts a suggested
fix on the pull request that you can commit or edit. It consumes no AI credits.

It covers a subset of the default and `security-extended` queries for C# and
JavaScript/TypeScript, among other languages. Code quality queries and alerts in
GitHub Actions workflows get no suggestions.

To check that it is on, go to **Settings → Advanced Security** and look for
**Copilot Autofix** in the "Code Security" section.

## Existing alerts: Agentic Autofix

### How it works

After every successful CodeQL scan of `dotnet` (push, weekly schedule or manual
run), the **CodeQL Agentic Autofix** workflow lists the open CodeQL alerts and
assigns some of them to Copilot (`copilot-swe-agent[bot]`). For each assignment,
Copilot explores the code, writes and validates a fix, and usually opens a draft
pull request within a few minutes. When you merge it, the next CodeQL scan of
`dotnet` closes the alert and the following workflow run assigns the next one.

The workflow picks alerts as follows:

- **Current alerts only.** Only alerts reported by
  `.github/workflows/codeql-analysis.yml` count. Alerts from configurations that
  no longer run, such as an old default setup, are ignored (see
  [Stale alerts](#stale-alerts)).
- **Security alerts only.** The alert's security severity must be `medium` or
  higher. Code quality alerts have no security severity and are never assigned.
- **Unassigned only.** Alerts assigned to a person are left alone, as are alerts
  in generated or library code.
- **Most severe first**, then oldest first.
- **One alert per rule.** The other alerts of a rule wait until the first one
  is fixed, so a pattern is fixed once before Copilot repeats it.
- **At most 5 rules with Copilot at a time.** A rule is "in flight" while any of
  its open alerts is assigned to Copilot. Alerts you assign together in the UI
  count once, because they share one agent session.

Every run writes a job summary listing what it assigned, which rules Copilot is
working on and how many alerts are waiting.

### Setup

1. **Turn on Agentic Autofix.** Go to **Settings → Advanced Security** and, in
   the "Code Security" section, set **Agentic Autofix** to **On**.
2. **Create a token.** Copilot only accepts assignments made with a user token,
   so the workflow's `GITHUB_TOKEN` can't assign alerts. Create a
   [fine-grained personal access token](https://github.com/settings/personal-access-tokens/new):
   - **Repository access:** Only select repositories → `ComicMaintainer`
   - **Permissions:** Code scanning alerts → **Read and write**
   - **Expiration:** your choice. When it expires, runs fail with HTTP 401.
3. **Store it.** Go to **Settings → Secrets and variables → Actions → New
   repository secret** and save the token as `CODEQL_AUTOFIX_TOKEN`.
4. **Set a budget (optional).** Agentic Autofix consumes Code Security AI
   credits even on public repositories. If your account has included AI credits
   from a Copilot plan, sessions use those first; after that, usage is metered.
   A budget in your account's **Settings → Billing and licensing → Budgets and
   alerts** caps the metered part.
5. **Try it.** Go to **Actions → CodeQL Agentic Autofix → Run workflow**, tick
   **dry-run** and check the job summary. Then run it again without dry-run, or
   wait for the next CodeQL scan of `dotnet`.

Until the secret exists, every run is a dry run: it reports what it would assign
and logs a warning, but assigns nothing.

### Tuning and pausing

A manual run (**Run workflow**) takes these inputs:

| Input | Default | Meaning |
| --- | --- | --- |
| `min-severity` | `medium` | Lowest security severity to assign (`critical`, `high`, `medium`, `low`) |
| `max-in-flight` | `5` | Most rules Copilot may work on at once; `0` assigns nothing |
| `dry-run` | off | Report what would be assigned without assigning |

Automatic runs use the defaults in `DEFAULTS` at the top of
[`.github/scripts/codeql-agentic-autofix.js`](../.github/scripts/codeql-agentic-autofix.js).
To pause automatic assignment, disable the workflow in the **Actions** tab.

### Reviewing Copilot's pull requests

Review Copilot's draft pull requests like any other. Copilot says in the pull
request if it couldn't validate the fix or thinks the alert is a false
positive. Mention `@copilot` in a comment to ask for changes.

A rule stays in flight until its alerts close, so unmerged pull requests hold
their slots. If you don't want Copilot's fix:

- **False positive or won't fix:** dismiss the alert.
- **You'll fix it yourself:** replace Copilot with yourself as the assignee. The
  workflow never touches alerts assigned to a person.
- **Try again:** unassign Copilot. The alert goes back in the queue and is
  assigned again on a later run.

### Tips

- **Repetitive rules.** For rules with many similar alerts, such as
  `actions/unpinned-tag`, select up to 25 of them in **Security and quality →
  Code scanning** and click **Assign to Copilot**. One agent session and one pull
  request then fix them all, instead of one session per alert. Leave out any
  alert that Copilot already has.
- **Custom instructions.** Copilot follows the repository's custom instructions
  ([`.github/copilot-instructions.md`](../.github/copilot-instructions.md)) when it
  writes a fix, so keep them current.

### Stale alerts

Alerts from a CodeQL configuration that no longer runs never close, even after
the code is fixed or removed. For example, an old default setup left alerts in a
`coveragereport/` folder that no longer exists. The workflow ignores them. To
clear them, open one of the alerts, click `dotnet` under **Affected branches**,
delete every configuration in the "Configurations analyzing" dialog except
`.github/workflows/codeql-analysis.yml`, then click **Save changes**.

### Limitations

- Agentic Autofix only works on alerts on the default branch.
- Copilot validates a fix by re-running CodeQL's default query suite. It can't
  confirm fixes for alerts from `security-extended` queries, which this
  repository runs through `security-and-quality`. The next CodeQL scan of
  `dotnet` closes the alert if the fix works.

### Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| Warning: `the CODEQL_AUTOFIX_TOKEN secret is not set` | Add the secret ([Setup](#setup), step 3). |
| HTTP 401 | The token has expired or was revoked. Create a new one and update the secret. |
| HTTP 403 | The token lacks **Code scanning alerts: Read and write**, or doesn't include this repository. |
| HTTP 400, or `GitHub did not list Copilot as an assignee` | Agentic Autofix is off. Turn it on ([Setup](#setup), step 1). |
| Alerts are assigned but no pull request appears | Also grant the token **Read and write** for Actions, Contents, Issues and Pull requests, which GitHub requires for assigning Copilot to issues. Then unassign Copilot from the alerts so they are assigned again. |
| Nothing is assigned | The job summary says why. Usually Copilot is at the limit: merge its pull requests, or dismiss or reassign the alerts it has whose fixes you don't want ([Reviewing](#reviewing-copilots-pull-requests)). |

## Files

- [`.github/workflows/codeql-agentic-autofix.yml`](../.github/workflows/codeql-agentic-autofix.yml): triggers and permissions
- [`.github/scripts/codeql-agentic-autofix.js`](../.github/scripts/codeql-agentic-autofix.js): picks and assigns alerts
- [`tests/js/codeql-agentic-autofix.test.js`](../tests/js/codeql-agentic-autofix.test.js): unit tests (`npm test`)

The workflow depends on the CodeQL workflow's name and path. If you rename or
move `codeql-analysis.yml`, update `workflows:` and `CODEQL_WORKFLOW_PATH` in
`codeql-agentic-autofix.yml`.
