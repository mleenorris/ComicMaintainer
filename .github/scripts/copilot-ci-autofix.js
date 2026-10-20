'use strict';

/**
 * Copilot CI Autofix: hands failing test runs to the Copilot cloud agent.
 *
 * Run by .github/workflows/copilot-ci-autofix.yml (through actions/github-script) when a watched
 * workflow completes:
 *
 * - Failed pull request runs: comments "@copilot ..." on the pull request with the failed jobs and
 *   log excerpts, so Copilot pushes a fix to the pull request's branch.
 * - Failed default-branch runs: opens a `ci-failure` issue and assigns it to Copilot, which opens a
 *   fix pull request. Later failures of the same workflow are added to the open issue as comments.
 * - Passing default-branch runs: close the workflow's open `ci-failure` issue.
 *
 * Copilot ignores comments written by bots and cannot be assigned with the workflow's GITHUB_TOKEN,
 * so issues and comments are written with COPILOT_AUTOFIX_TOKEN (`userClient`): a token of a user
 * with a Copilot license and write access. GITHUB_TOKEN (`github`) only reads runs, jobs and logs.
 *
 * See docs/COPILOT_CI_AUTOFIX.md.
 */

const COPILOT_ASSIGNEE = 'copilot-swe-agent[bot]';
// Copilot cloud agent sessions show up as "dynamic" workflow runs on the branch they work on.
const COPILOT_SESSION_PATH_PREFIX = 'dynamic/copilot-swe-agent/';
const OPT_OUT_LABEL = 'no-copilot-autofix';
const CI_FAILURE_LABEL = 'ci-failure';
const ISSUE_LABELS = [
    { name: CI_FAILURE_LABEL, color: 'b60205', description: 'A test workflow is failing on the default branch' },
    { name: 'automated', color: 'ededed', description: 'Opened automatically' },
];
const DEFAULT_MAX_ATTEMPTS = 3;
const FAILED_CONCLUSIONS = new Set(['failure', 'timed_out']);

const REQUEST_MARKER_PATTERN = /<!-- copilot-ci-autofix:request workflow=(\d+) sha=([0-9a-f]+) -->/g;
const EXHAUSTED_MARKER = '<!-- copilot-ci-autofix:exhausted -->';

const SESSION_POLL_INTERVAL_MS = 60 * 1000;
const SESSION_MAX_WAIT_MS = 50 * 60 * 1000;

const MAX_JOBS_WITH_EXCERPTS = 3;
const MAX_EXCERPT_LINES = 120;
const MAX_EXCERPT_CHARS = 12000;
const MAX_BLOCK_LINES = 40;
const MAX_LINE_CHARS = 500;
const FALLBACK_TAIL_LINES = 40;
// GitHub rejects issue and comment bodies longer than 65,536 characters.
const MAX_BODY_CHARS = 60000;

const TIMESTAMP_PREFIX = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z ?/;
// eslint-disable-next-line no-control-regex
const ANSI_ESCAPE = /\u001b\[[0-9;]*[A-Za-z]/g;
// A failed test from `dotnet test` ("  Failed Name [12 ms]") or node:test ("✖ name", "not ok 3 - name").
const FAILED_TEST = /^\s*(?:Failed\s+\S|✖\s+\S|not ok \d+\b)/;
// The next test result ends the previous failure's details.
const TEST_RESULT = /^\s*(?:(?:Passed|Failed|Skipped)\s+\S|[✔✖]\s|(?:not )?ok \d+\b)/;
// Compiler, MSBuild and NuGet errors, e.g. "Foo.cs(1,2): error CS0103: ...".
const BUILD_ERROR = /\berror [A-Z]{2,}\d{3,}\b/;
const ERROR_ANNOTATION = /^##\[error\]/;
// Framework and runtime stack frames; the frames in this repository's code are the useful ones.
const FRAMEWORK_FRAME = /^\s*at (?:(?:Microsoft|System|Xunit)\.|node:|.*\((?:node:.*|<anonymous>|index \d+)\)(?: \{)?$)/;

const FIX_GUIDELINES = [
    '- Find and fix the root cause. Change a test only if the test itself is wrong; do not skip, delete or weaken tests to make the run pass.',
    '- Run the failing tests locally and make sure they pass before you finish.',
].join('\n');

function parseMaxAttempts(value) {
    const text = String(value ?? '').trim();
    const parsed = /^\d+$/.test(text) ? Number(text) : NaN;
    return parsed >= 1 ? parsed : DEFAULT_MAX_ATTEMPTS;
}

function cleanLogLine(line) {
    const cleaned = line.replace(TIMESTAMP_PREFIX, '').replace(ANSI_ESCAPE, '').trimEnd();
    if (cleaned.length <= MAX_LINE_CHARS) {
        return cleaned;
    }
    const cut = /[\uD800-\uDBFF]/.test(cleaned[MAX_LINE_CHARS - 1]) ? MAX_LINE_CHARS - 1 : MAX_LINE_CHARS;
    return `${cleaned.slice(0, cut)}…`;
}

// Lines that belong to the failure above them: indented details (which can contain blank lines),
// stack traces and .NET's "--- End of stack trace ---" separators.
function isFailureDetail(line) {
    return (line === '' || /^(?:\s|---)/.test(line)) && !TEST_RESULT.test(line);
}

function truncateExcerpt(text) {
    let lines = text.split('\n');
    let truncated = false;
    if (lines.length > MAX_EXCERPT_LINES) {
        lines = lines.slice(0, MAX_EXCERPT_LINES);
        truncated = true;
    }
    let result = lines.join('\n');
    if (result.length > MAX_EXCERPT_CHARS) {
        result = result.slice(0, result.lastIndexOf('\n', MAX_EXCERPT_CHARS));
        truncated = true;
    }
    return truncated ? `${result}\n… (truncated; see the full log)` : result;
}

/**
 * Picks the useful part of a job log: failed tests with their error details, and build errors.
 * When neither is recognisable (a script or setup step failed), falls back to the output that led
 * up to the first error annotation.
 */
function extractLogExcerpt(log) {
    const lines = String(log ?? '').split(/\r?\n/).map(cleanLogLine);
    const sections = [];
    const seen = new Set();
    const add = text => {
        if (!seen.has(text)) {
            seen.add(text);
            sections.push(text);
        }
    };

    for (let i = 0; i < lines.length; i++) {
        if (FAILED_TEST.test(lines[i])) {
            const block = [lines[i]];
            let next = i + 1;
            for (; next < lines.length && isFailureDetail(lines[next]); next++) {
                if (block.length < MAX_BLOCK_LINES && !FRAMEWORK_FRAME.test(lines[next])) {
                    block.push(lines[next]);
                }
            }
            while (block[block.length - 1] === '') {
                block.pop();
            }
            add(block.join('\n'));
            i = next - 1;
        } else if (BUILD_ERROR.test(lines[i])) {
            add(lines[i]);
        }
    }

    if (sections.length === 0) {
        const errorIndex = lines.findIndex(line => ERROR_ANNOTATION.test(line));
        const end = errorIndex === -1 ? lines.length : errorIndex + 1;
        sections.push(lines.slice(0, end).filter(line => line.trim() !== '').slice(-FALLBACK_TAIL_LINES).join('\n'));
    }

    return truncateExcerpt(sections.join('\n'));
}

function codeFence(text) {
    const longestRun = Math.max(2, ...Array.from(String(text).matchAll(/`+/g), match => match[0].length));
    const fence = '`'.repeat(longestRun + 1);
    return `${fence}text\n${text}\n${fence}`;
}

function inlineCode(text) {
    return `\`${String(text ?? '').replace(/`/g, "'").replace(/\s+/g, ' ').trim()}\``;
}

function escapeHtml(text) {
    const entities = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' };
    return String(text ?? '').replace(/[&<>"]/g, character => entities[character]);
}

function requestMarker(run) {
    return `<!-- copilot-ci-autofix:request workflow=${run.workflow_id} sha=${run.head_sha} -->`;
}

function issueMarker(run) {
    return `<!-- copilot-ci-autofix:issue workflow=${run.workflow_id} -->`;
}

function parseRequestMarkers(body) {
    return Array.from(String(body ?? '').matchAll(REQUEST_MARKER_PATTERN), match => ({ workflowId: match[1], sha: match[2] }));
}

function formatRunReference({ run, workflowName, serverUrl, repoFullName }) {
    const attempt = run.run_attempt > 1 ? ` (attempt ${run.run_attempt})` : '';
    const sha = String(run.head_sha);
    return `[${inlineCode(`${workflowName} #${run.run_number}`)}](${run.html_url})${attempt} for commit `
        + `[\`${sha.slice(0, 7)}\`](${serverUrl}/${repoFullName}/commit/${sha})`;
}

function formatFailedJobs(jobs, inheritedJobNames = new Set(), baseBranch = '') {
    if (jobs.length === 0) {
        return '- No failed jobs were reported; open the run for details.';
    }
    return jobs.map(job => {
        const steps = job.failedSteps.length === 0
            ? ''
            : ` — failed step${job.failedSteps.length > 1 ? 's' : ''}: ${job.failedSteps.map(inlineCode).join(', ')}`;
        const inherited = inheritedJobNames.has(job.name)
            ? ` (also failing on ${inlineCode(baseBranch)}, so probably not caused by this pull request)`
            : '';
        return `- [${inlineCode(job.name)}](${job.url})${steps}${inherited}`;
    }).join('\n');
}

function formatExcerpts(jobs, excerpts) {
    return jobs.filter(job => excerpts.has(job.id)).map(job => [
        '<details>',
        `<summary>Log excerpt: ${escapeHtml(job.name)}</summary>`,
        '',
        codeFence(excerpts.get(job.id)),
        '',
        '</details>',
    ].join('\n')).join('\n\n');
}

// Joins the parts of a body, dropping the log excerpts if the result would be too long to post.
function joinBody(parts, excerptsIndex) {
    const body = parts.filter(Boolean).join('\n\n');
    if (body.length <= MAX_BODY_CHARS || !parts[excerptsIndex]) {
        return body;
    }
    const withoutExcerpts = [...parts];
    withoutExcerpts[excerptsIndex] = '_The log excerpts were too long to include; open the failed jobs above for the logs._';
    return withoutExcerpts.filter(Boolean).join('\n\n');
}

function buildPullRequestComment({ run, workflowName, serverUrl, repoFullName, jobs, excerpts, inheritedJobNames, baseBranch, attempt, maxAttempts, autofixRunUrl }) {
    return joinBody([
        `@copilot The ${inlineCode(workflowName)} workflow failed on this pull request. Please investigate the failure and push a fix to this branch.`,
        FIX_GUIDELINES,
        `**Failed run:** ${formatRunReference({ run, workflowName, serverUrl, repoFullName })}`,
        `**Failed jobs:**\n${formatFailedJobs(jobs, inheritedJobNames, baseBranch)}`,
        formatExcerpts(jobs, excerpts),
        `<sub>Automatic fix request ${attempt} of ${maxAttempts} from [Copilot CI Autofix](${autofixRunUrl}). `
            + `Add the ${inlineCode(OPT_OUT_LABEL)} label to stop these requests on this pull request.</sub>`,
        requestMarker(run),
    ], 4);
}

// Deliberately does not mention @copilot: this note must not start another session.
function buildExhaustedComment({ run, workflowName, serverUrl, repoFullName, maxAttempts }) {
    return [
        `Copilot CI Autofix has asked Copilot to fix failing checks on this pull request ${maxAttempts} `
            + `time${maxAttempts === 1 ? '' : 's'}, which is the limit, so it will not ask again. `
            + `The latest failure is ${formatRunReference({ run, workflowName, serverUrl, repoFullName })}.`,
        'To have Copilot try again, mention it in a comment on this pull request yourself.',
        EXHAUSTED_MARKER,
    ].join('\n\n');
}

function buildIssueTitle({ workflowName, branch }) {
    return `CI failure: ${workflowName} is failing on ${branch}`;
}

function buildIssueBody({ run, workflowName, serverUrl, repoFullName, branch, jobs, excerpts, autofixRunUrl }) {
    return joinBody([
        `The ${inlineCode(workflowName)} workflow is failing on ${inlineCode(branch)}, the default branch.`,
        `**Task:** investigate the failure below, fix it, and open a pull request against ${inlineCode(branch)} that makes the workflow pass again.`,
        FIX_GUIDELINES,
        `**Failed run:** ${formatRunReference({ run, workflowName, serverUrl, repoFullName })}`,
        `**Failed jobs:**\n${formatFailedJobs(jobs)}`,
        formatExcerpts(jobs, excerpts),
        `<sub>Opened by [Copilot CI Autofix](${autofixRunUrl}). While this issue is open, later failures of this workflow on `
            + `${inlineCode(branch)} are added to it as comments instead of opening new issues.</sub>`,
        issueMarker(run),
    ], 5);
}

function buildRecurrenceComment({ run, workflowName, serverUrl, repoFullName, branch, jobs }) {
    return [
        `The ${inlineCode(workflowName)} workflow failed again on ${inlineCode(branch)}: ${formatRunReference({ run, workflowName, serverUrl, repoFullName })}.`,
        `**Failed jobs:**\n${formatFailedJobs(jobs)}`,
    ].join('\n\n');
}

function buildResolvedComment({ run, workflowName, serverUrl, repoFullName, branch }) {
    return [
        `The ${inlineCode(workflowName)} workflow passes again on ${inlineCode(branch)}: ${formatRunReference({ run, workflowName, serverUrl, repoFullName })}. Closing this issue.`,
        'If a pull request opened for this issue is no longer needed, close it as well.',
    ].join('\n\n');
}

async function finish(core, action, message, details = {}) {
    core.info(message);
    try {
        await core.summary.addHeading('Copilot CI Autofix', 3).addRaw(message, true).write();
    } catch {
        // The job summary is informational only.
    }
    return { action, message, ...details };
}

function defaultSleep(milliseconds) {
    return new Promise(resolve => setTimeout(resolve, milliseconds));
}

async function listFailedJobs(github, repo, runId) {
    const jobs = await github.paginate(github.rest.actions.listJobsForWorkflowRun, { ...repo, run_id: runId, filter: 'latest', per_page: 100 });
    return jobs.filter(job => FAILED_CONCLUSIONS.has(job.conclusion)).map(job => ({
        id: job.id,
        name: job.name,
        url: job.html_url,
        failedSteps: (job.steps ?? []).filter(step => FAILED_CONCLUSIONS.has(step.conclusion)).map(step => step.name),
    }));
}

async function collectExcerpts({ github, core, repo }, jobs) {
    const excerpts = new Map();
    for (const job of jobs.slice(0, MAX_JOBS_WITH_EXCERPTS)) {
        try {
            const { data } = await github.rest.actions.downloadJobLogsForWorkflowRun({ ...repo, job_id: job.id });
            excerpts.set(job.id, extractLogExcerpt(typeof data === 'string' ? data : Buffer.from(data).toString('utf8')));
        } catch (error) {
            core.warning(`Could not download the log of job "${job.name}": ${error.message}`);
        }
    }
    return excerpts;
}

async function getPullRequest(userClient, repo, number) {
    const { data } = await userClient.rest.pulls.get({ ...repo, pull_number: number });
    return data;
}

// Prefers the open pull request whose head is the failed commit; otherwise returns any open one so
// the caller can report it as outdated.
async function findPullRequest({ userClient, repo, run }) {
    const numbers = (run.pull_requests ?? []).map(pullRequest => pullRequest.number);
    if (numbers.length === 0 && run.head_branch) {
        const { data } = await userClient.rest.pulls.list({ ...repo, state: 'open', head: `${repo.owner}:${run.head_branch}`, per_page: 10 });
        numbers.push(...data.map(pullRequest => pullRequest.number));
    }
    let fallback = null;
    for (const number of numbers) {
        const pullRequest = await getPullRequest(userClient, repo, number);
        if (pullRequest.state !== 'open') {
            continue;
        }
        if (pullRequest.head.sha === run.head_sha) {
            return pullRequest;
        }
        fallback ??= pullRequest;
    }
    return fallback;
}

function pullRequestSkipReason(pullRequest, run, repoFullName) {
    if (pullRequest.state !== 'open') {
        return `#${pullRequest.number} is no longer open.`;
    }
    if (pullRequest.head?.repo?.full_name !== repoFullName) {
        return `#${pullRequest.number} comes from a fork.`;
    }
    if (pullRequest.head.sha !== run.head_sha) {
        return `#${pullRequest.number} has commits newer than the failed run; the run for its latest commit is handled instead.`;
    }
    if ((pullRequest.labels ?? []).some(label => label.name === OPT_OUT_LABEL)) {
        return `#${pullRequest.number} has the ${OPT_OUT_LABEL} label.`;
    }
    return null;
}

// Jobs of the failed run that also failed in the latest completed push run of the same workflow on
// the pull request's base branch. Those failures are most likely inherited rather than caused by
// the pull request; on the default branch they are tracked by a `ci-failure` issue instead.
async function findInheritedFailures({ github, core, repo, run }, baseBranch, failedJobs) {
    if (failedJobs.length === 0) {
        return new Set();
    }
    try {
        const { data } = await github.rest.actions.listWorkflowRuns({
            ...repo, workflow_id: run.workflow_id, branch: baseBranch, event: 'push', status: 'completed', per_page: 10,
        });
        const latest = data.workflow_runs.find(baseRun => baseRun.conclusion === 'success' || baseRun.conclusion === 'failure');
        if (!latest || latest.conclusion !== 'failure') {
            return new Set();
        }
        const baseFailures = new Set((await listFailedJobs(github, repo, latest.id)).map(job => job.name));
        return new Set(failedJobs.map(job => job.name).filter(name => baseFailures.has(name)));
    } catch (error) {
        core.warning(`Could not compare the failure with ${baseBranch}: ${error.message}`);
        return new Set();
    }
}

async function listOpenCiFailureIssues(userClient, repo) {
    const issues = await userClient.paginate(userClient.rest.issues.listForRepo, { ...repo, state: 'open', labels: CI_FAILURE_LABEL, per_page: 100 });
    return issues.filter(issue => !issue.pull_request);
}

// Whether the pull request refers to an open `ci-failure` issue, as the pull requests Copilot opens
// for those issues do ("Fixes #12"). Such a pull request is meant to fix the failures it inherits.
async function referencesCiFailureIssue({ userClient, core, repo }, pullRequest) {
    try {
        const text = `${pullRequest.title ?? ''}\n${pullRequest.body ?? ''}`;
        const issues = await listOpenCiFailureIssues(userClient, repo);
        return issues.some(issue => new RegExp(`(?:#|/issues/)${issue.number}(?!\\d)`).test(text));
    } catch (error) {
        core.warning(`Could not check whether #${pullRequest.number} fixes a ci-failure issue: ${error.message}`);
        return false;
    }
}

// A newer run of the workflow on the same branch for another commit, if any. That run reflects the
// current state of the branch and is handled when it completes, so this run needs no action.
async function findNewerRun({ github, core, repo, run }) {
    try {
        const { data } = await github.rest.actions.listWorkflowRuns({ ...repo, workflow_id: run.workflow_id, branch: run.head_branch, per_page: 20 });
        return data.workflow_runs.find(other => other.run_number > run.run_number
            && other.head_sha !== run.head_sha
            && other.event !== 'pull_request'
            && !(other.status === 'completed' && ['cancelled', 'skipped'].includes(other.conclusion))) ?? null;
    } catch (error) {
        core.warning(`Could not check for newer runs of the workflow: ${error.message}`);
        return null;
    }
}

async function isCopilotWorking({ github, core, repo }, branch) {
    try {
        const { data } = await github.rest.actions.listWorkflowRunsForRepo({ ...repo, branch, event: 'dynamic', per_page: 20 });
        return data.workflow_runs.some(session => session.status !== 'completed' && String(session.path ?? '').startsWith(COPILOT_SESSION_PATH_PREFIX));
    } catch (error) {
        core.warning(`Could not check whether Copilot is working on ${branch}: ${error.message}`);
        return false;
    }
}

/**
 * Waits for a Copilot session that is already working on the branch to finish, so a request is not
 * posted while Copilot may still be fixing the failure. Returns 'idle', 'waited' or 'timed-out'.
 */
async function waitForCopilotSession(ctx, branch) {
    let waitedMs = 0;
    while (await isCopilotWorking(ctx, branch)) {
        if (waitedMs >= SESSION_MAX_WAIT_MS) {
            return 'timed-out';
        }
        if (waitedMs === 0) {
            ctx.core.info(`Copilot is working on ${branch}; waiting for its session to finish.`);
        }
        await ctx.sleep(SESSION_POLL_INTERVAL_MS);
        waitedMs += SESSION_POLL_INTERVAL_MS;
    }
    return waitedMs === 0 ? 'idle' : 'waited';
}

async function handlePullRequestFailure(ctx) {
    const { github, userClient, core, repo, run, maxAttempts } = ctx;

    let pullRequest = await findPullRequest(ctx);
    if (!pullRequest) {
        return finish(core, 'skipped', 'No open pull request matches the failed run.');
    }
    let skipReason = pullRequestSkipReason(pullRequest, run, ctx.repoFullName);
    if (skipReason) {
        return finish(core, 'skipped', skipReason, { pullRequest: pullRequest.number });
    }

    const jobs = await listFailedJobs(github, repo, run.id);
    const baseBranch = pullRequest.base.ref;
    let inheritedJobNames = await findInheritedFailures(ctx, baseBranch, jobs);
    if (inheritedJobNames.size > 0 && await referencesCiFailureIssue(ctx, pullRequest)) {
        inheritedJobNames = new Set();
    }
    if (jobs.length > 0 && jobs.every(job => inheritedJobNames.has(job.name))) {
        return finish(core, 'skipped',
            `Every failed job of #${pullRequest.number} is also failing on ${baseBranch}, so the failure is probably not caused by the pull request.`,
            { pullRequest: pullRequest.number });
    }

    const session = await waitForCopilotSession(ctx, run.head_branch);
    if (session === 'timed-out') {
        return finish(core, 'skipped', `Copilot is still working on #${pullRequest.number}; not asking it to fix this run.`, { pullRequest: pullRequest.number });
    }
    if (session === 'waited') {
        pullRequest = await getPullRequest(userClient, repo, pullRequest.number);
        skipReason = pullRequestSkipReason(pullRequest, run, ctx.repoFullName);
        if (skipReason) {
            return finish(core, 'skipped', skipReason, { pullRequest: pullRequest.number });
        }
    }

    // One request per commit: when several watched workflows fail on the same commit, Copilot is
    // asked once, and any failure its fix does not cover is reported again for the new commit.
    const comments = await userClient.paginate(userClient.rest.issues.listComments, { ...repo, issue_number: pullRequest.number, per_page: 100 });
    const requests = comments.flatMap(comment => parseRequestMarkers(comment.body));
    if (requests.some(request => request.sha === run.head_sha)) {
        return finish(core, 'skipped', `Copilot was already asked to fix commit ${run.head_sha} in #${pullRequest.number}.`, { pullRequest: pullRequest.number });
    }
    if (requests.length >= maxAttempts) {
        if (!comments.some(comment => String(comment.body ?? '').includes(EXHAUSTED_MARKER))) {
            await userClient.rest.issues.createComment({ ...repo, issue_number: pullRequest.number, body: buildExhaustedComment(ctx) });
        }
        return finish(core, 'exhausted', `Copilot was already asked ${requests.length} times to fix #${pullRequest.number}, the limit of ${maxAttempts}.`, { pullRequest: pullRequest.number });
    }

    const excerpts = await collectExcerpts(ctx, jobs);
    const attempt = requests.length + 1;
    const body = buildPullRequestComment({ ...ctx, jobs, excerpts, inheritedJobNames, baseBranch, attempt });
    const { data: comment } = await userClient.rest.issues.createComment({ ...repo, issue_number: pullRequest.number, body });
    return finish(core, 'requested', `Asked Copilot to fix #${pullRequest.number} (request ${attempt} of ${maxAttempts}): ${comment.html_url}`, { pullRequest: pullRequest.number, attempt });
}

async function ensureLabels(userClient, repo) {
    for (const label of ISSUE_LABELS) {
        try {
            await userClient.rest.issues.getLabel({ ...repo, name: label.name });
        } catch (error) {
            if (error.status !== 404) {
                throw error;
            }
            try {
                await userClient.rest.issues.createLabel({ ...repo, ...label });
            } catch (createError) {
                // 422: another run created the label in the meantime.
                if (createError.status !== 422) {
                    throw createError;
                }
            }
        }
    }
}

async function assignCopilot({ userClient, core, repo, repoFullName }, issueNumber, branch) {
    try {
        const { data } = await userClient.rest.issues.addAssignees({
            ...repo,
            issue_number: issueNumber,
            assignees: [COPILOT_ASSIGNEE],
            agent_assignment: { target_repo: repoFullName, base_branch: branch },
        });
        if ((data.assignees ?? []).some(assignee => /^copilot/i.test(assignee.login))) {
            return true;
        }
        core.warning(`GitHub did not assign Copilot to #${issueNumber}. Check that Copilot cloud agent is enabled for this repository and that the owner of COPILOT_AUTOFIX_TOKEN has a Copilot license.`);
    } catch (error) {
        core.warning(`Could not assign Copilot to #${issueNumber}: ${error.message}`);
    }
    return false;
}

async function findOpenIssue({ userClient, repo, run }) {
    const marker = issueMarker(run);
    return (await listOpenCiFailureIssues(userClient, repo)).find(issue => String(issue.body ?? '').includes(marker)) ?? null;
}

async function handleDefaultBranchFailure(ctx, branch) {
    const { github, userClient, core, repo, run } = ctx;
    const newerRun = await findNewerRun(ctx);
    if (newerRun) {
        return finish(core, 'skipped', `A newer run of the workflow on ${branch} (${newerRun.html_url}) is handled instead.`);
    }
    const jobs = await listFailedJobs(github, repo, run.id);

    const existing = await findOpenIssue(ctx);
    if (existing) {
        const { data: comment } = await userClient.rest.issues.createComment({
            ...repo, issue_number: existing.number, body: buildRecurrenceComment({ ...ctx, branch, jobs }),
        });
        return finish(core, 'issue-updated', `#${existing.number} already tracks this failure; added the new run to it: ${comment.html_url}`, { issue: existing.number });
    }

    const excerpts = await collectExcerpts(ctx, jobs);
    await ensureLabels(userClient, repo);
    const { data: issue } = await userClient.rest.issues.create({
        ...repo,
        title: buildIssueTitle({ ...ctx, branch }),
        body: buildIssueBody({ ...ctx, branch, jobs, excerpts }),
        labels: ISSUE_LABELS.map(label => label.name),
    });

    const assigned = await assignCopilot(ctx, issue.number, branch);
    if (!assigned) {
        core.setFailed(`Opened #${issue.number} for the failure but could not assign it to Copilot; see the warning above.`);
    }
    return finish(core, 'issue-opened',
        assigned ? `Opened ${issue.html_url} and assigned it to Copilot.` : `Opened ${issue.html_url} but could not assign it to Copilot.`,
        { issue: issue.number, assigned });
}

async function handleDefaultBranchSuccess(ctx, branch) {
    const { userClient, core, repo } = ctx;
    const existing = await findOpenIssue(ctx);
    if (!existing) {
        return finish(core, 'skipped', `The workflow passes on ${branch} and no open issue tracks a failure of it.`);
    }
    const newerRun = await findNewerRun(ctx);
    if (newerRun) {
        return finish(core, 'skipped', `A newer run of the workflow on ${branch} (${newerRun.html_url}) decides whether #${existing.number} is resolved.`, { issue: existing.number });
    }
    await userClient.rest.issues.createComment({ ...repo, issue_number: existing.number, body: buildResolvedComment({ ...ctx, branch }) });
    await userClient.rest.issues.update({ ...repo, issue_number: existing.number, state: 'closed', state_reason: 'completed' });
    return finish(core, 'issue-closed', `The workflow passes again on ${branch}; closed #${existing.number}.`, { issue: existing.number });
}

/**
 * Entry point, called by the workflow with the objects actions/github-script provides.
 * `env` and `sleep` can be replaced in tests.
 */
async function handleCompletedRun({ github, context, core, getOctokit, env = process.env, sleep = defaultSleep }) {
    const run = context.payload.workflow_run;
    const repo = { owner: context.repo.owner, repo: context.repo.repo };
    const repoFullName = `${repo.owner}/${repo.repo}`;

    if (!run || (run.conclusion !== 'failure' && run.conclusion !== 'success')) {
        return finish(core, 'skipped', 'The run neither failed nor passed.');
    }
    if (run.head_repository?.full_name !== repoFullName) {
        return finish(core, 'skipped', 'The run is for a fork; fork runs are not handed to Copilot.');
    }
    const failed = run.conclusion === 'failure';
    const isPullRequest = run.event === 'pull_request';
    const defaultBranch = context.payload.repository?.default_branch;
    const onDefaultBranch = !isPullRequest && Boolean(defaultBranch) && run.head_branch === defaultBranch;
    if (!isPullRequest && !onDefaultBranch) {
        return finish(core, 'skipped', `Runs of ${run.event} events outside the default branch are not handed to Copilot.`);
    }
    if (!failed && !onDefaultBranch) {
        return finish(core, 'skipped', 'The pull request run passed.');
    }

    const token = String(env.COPILOT_AUTOFIX_TOKEN ?? '').trim();
    if (!token) {
        if (failed) {
            core.warning('COPILOT_AUTOFIX_TOKEN is not set, so the failure was not handed to Copilot. See docs/COPILOT_CI_AUTOFIX.md.');
        }
        return finish(core, 'skipped', 'COPILOT_AUTOFIX_TOKEN is not set.');
    }

    const ctx = {
        github,
        userClient: getOctokit(token),
        core,
        repo,
        repoFullName,
        run,
        sleep,
        workflowName: context.payload.workflow?.name || run.name || 'workflow',
        serverUrl: context.serverUrl,
        autofixRunUrl: `${context.serverUrl}/${repoFullName}/actions/runs/${context.runId}`,
        maxAttempts: parseMaxAttempts(env.COPILOT_AUTOFIX_MAX_ATTEMPTS),
    };

    if (isPullRequest) {
        return handlePullRequestFailure(ctx);
    }
    return failed ? handleDefaultBranchFailure(ctx, defaultBranch) : handleDefaultBranchSuccess(ctx, defaultBranch);
}

module.exports = {
    handleCompletedRun,
    extractLogExcerpt,
    codeFence,
    inlineCode,
    parseMaxAttempts,
    parseRequestMarkers,
    COPILOT_ASSIGNEE,
    OPT_OUT_LABEL,
    SESSION_MAX_WAIT_MS,
};
