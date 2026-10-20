'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    handleCompletedRun,
    extractLogExcerpt,
    codeFence,
    inlineCode,
    parseMaxAttempts,
    parseRequestMarkers,
    COPILOT_ASSIGNEE,
    OPT_OUT_LABEL,
    SESSION_MAX_WAIT_MS,
} = require('../../.github/scripts/copilot-ci-autofix.js');

const OWNER = 'octo';
const REPO = 'comics';
const SHA = 'a1b2c3d4'.padEnd(40, '0');
const NEW_SHA = 'f9e8d7c6'.padEnd(40, '0');
const WORKFLOW_ID = 555;
const RUN_ID = 100;
const BASE_RUN_ID = 200;
const WRITE_METHODS = new Set(['createComment', 'create', 'update', 'addAssignees', 'createLabel']);

const DOTNET_LOG = [
    '2026-10-19T05:21:19.0000000Z Starting test execution, please wait...',
    '2026-10-19T05:21:20.0000000Z   Passed ComicMaintainer.Tests.Services.SettingsServiceTests.Loads [12 ms]',
    '2026-10-19T05:21:20.5000000Z   Failed ComicMaintainer.Tests.Integration.SwaggerTests.CanBeEnabled [3 s]',
    '2026-10-19T05:21:20.5000000Z   Error Message:',
    "2026-10-19T05:21:20.5000000Z    Microsoft.Data.Sqlite.SqliteException : SQLite Error 19: 'UNIQUE constraint failed: ScheduledJobs.JobKey'.",
    '2026-10-19T05:21:20.5000000Z   Stack Trace:',
    '2026-10-19T05:21:20.5000000Z      at Microsoft.Data.Sqlite.SqliteException.ThrowExceptionForRC(Int32 rc, sqlite3 db)',
    '2026-10-19T05:21:20.5000000Z      at ComicMaintainer.Core.Services.ScheduledJobService.SeedDefaultsAsync() in /src/ScheduledJobService.cs:line 87',
    '2026-10-19T05:21:20.5000000Z --- End of stack trace from previous location ---',
    '2026-10-19T05:21:20.5000000Z      at System.Threading.Tasks.Task.Wait()',
    '2026-10-19T05:21:21.0000000Z [05:21:21] [INF] Application is shutting down...',
    '2026-10-19T05:21:22.0000000Z Test Run Failed.',
    '2026-10-19T05:21:22.0000000Z ##[error]Process completed with exit code 1.',
].join('\n');

const DOTNET_EXCERPT = [
    '  Failed ComicMaintainer.Tests.Integration.SwaggerTests.CanBeEnabled [3 s]',
    '  Error Message:',
    "   Microsoft.Data.Sqlite.SqliteException : SQLite Error 19: 'UNIQUE constraint failed: ScheduledJobs.JobKey'.",
    '  Stack Trace:',
    '     at ComicMaintainer.Core.Services.ScheduledJobService.SeedDefaultsAsync() in /src/ScheduledJobService.cs:line 87',
    '--- End of stack trace from previous location ---',
].join('\n');

test('extractLogExcerpt keeps a dotnet test failure without timestamps, framework frames or app logs', () => {
    assert.equal(extractLogExcerpt(DOTNET_LOG), DOTNET_EXCERPT);
});

test('extractLogExcerpt reads node:test failures, keeping blank lines inside the error details', () => {
    const log = [
        '\u001b[31m✖ formats a title \u001b[90m(1.2ms)\u001b[39m',
        '\u001b[32m✔ parses a page \u001b[90m(0.4ms)\u001b[39m',
        '\u001b[31m✖ failing tests:\u001b[39m',
        '',
        'test at tests/js/title.test.js:5:1',
        '\u001b[31m✖ formats a title \u001b[90m(1.2ms)\u001b[39m',
        '  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:',
        '  ',
        "  'a' !== 'b'",
        '  ',
        '      at TestContext.<anonymous> (/work/tests/js/title.test.js:5:47)',
        '      at Test.runInAsyncScope (node:async_hooks:227:14)',
        '      at new Promise (<anonymous>)',
        '',
        'ℹ tests 2',
    ].join('\n');

    assert.equal(extractLogExcerpt(log), [
        '✖ formats a title (1.2ms)',
        '✖ failing tests:',
        '✖ formats a title (1.2ms)',
        '  AssertionError [ERR_ASSERTION]: Expected values to be strictly equal:',
        '',
        "  'a' !== 'b'",
        '',
        '      at TestContext.<anonymous> (/work/tests/js/title.test.js:5:47)',
    ].join('\n'));
});

test('extractLogExcerpt lists each build error once', () => {
    const error = "/src/Foo.cs(10,5): error CS0103: The name 'x' does not exist in the current context [/src/Foo.csproj]";
    const log = [`2026-10-19T05:00:00.0000000Z ${error}`, 'Build FAILED.', error, '##[error]Process completed with exit code 1.'].join('\n');

    assert.equal(extractLogExcerpt(log), error);
});

test('extractLogExcerpt falls back to the output before the first error annotation', () => {
    const log = ['Run npm ci', '', 'npm ERR! code ENOTFOUND', '##[error]Process completed with exit code 1.', 'Post job cleanup.'].join('\r\n');

    assert.equal(extractLogExcerpt(log), 'Run npm ci\nnpm ERR! code ENOTFOUND\n##[error]Process completed with exit code 1.');
    assert.equal(extractLogExcerpt(undefined), '');
});

test('extractLogExcerpt caps long lines and long excerpts', () => {
    assert.equal(extractLogExcerpt(`${'a'.repeat(499)}😀${'b'.repeat(100)}`), `${'a'.repeat(499)}…`);

    const failures = Array.from({ length: 200 }, (_, index) => `  Failed Test${index} [1 ms]\n  Error Message:\n   ${'x'.repeat(300)}${index}`).join('\n');
    const excerpt = extractLogExcerpt(failures);
    assert.ok(excerpt.endsWith('\n… (truncated; see the full log)'));
    assert.ok(excerpt.length < 12100, `excerpt is ${excerpt.length} characters long`);
    assert.ok(excerpt.split('\n').length <= 121);
});

test('codeFence uses a fence longer than any backtick run in the text', () => {
    assert.equal(codeFence('plain'), '```text\nplain\n```');
    assert.equal(codeFence('has ``` and `` inside'), '````text\nhas ``` and `` inside\n````');
});

test('inlineCode keeps names on one line and free of backticks', () => {
    assert.equal(inlineCode('Run `npm test`\nnow'), "`Run 'npm test' now`");
});

test('parseMaxAttempts accepts positive whole numbers and defaults to 3', () => {
    assert.equal(parseMaxAttempts('5'), 5);
    assert.equal(parseMaxAttempts(' 1 '), 1);
    for (const value of [undefined, '', '0', '-1', '2.5', 'abc', '1e3']) {
        assert.equal(parseMaxAttempts(value), 3, `value ${value}`);
    }
});

test('parseRequestMarkers finds every request marker in a comment', () => {
    const body = `text\n<!-- copilot-ci-autofix:request workflow=1 sha=${SHA} -->\n<!-- copilot-ci-autofix:request workflow=2 sha=abc -->`;
    assert.deepEqual(parseRequestMarkers(body), [{ workflowId: '1', sha: SHA }, { workflowId: '2', sha: 'abc' }]);
    assert.deepEqual(parseRequestMarkers(undefined), []);
});

function pullRequestRun(overrides = {}) {
    return {
        id: RUN_ID,
        run_number: 42,
        run_attempt: 1,
        workflow_id: WORKFLOW_ID,
        name: '.NET Tests',
        event: 'pull_request',
        status: 'completed',
        conclusion: 'failure',
        head_branch: 'feature',
        head_sha: SHA,
        html_url: `https://github.com/${OWNER}/${REPO}/actions/runs/${RUN_ID}`,
        head_repository: { full_name: `${OWNER}/${REPO}` },
        pull_requests: [{ number: 7 }],
        ...overrides,
    };
}

function defaultBranchRun(overrides = {}) {
    return pullRequestRun({ event: 'push', head_branch: 'dotnet', pull_requests: [], ...overrides });
}

function pullRequest(overrides = {}) {
    return {
        number: 7,
        state: 'open',
        title: 'Add a feature',
        body: '',
        labels: [],
        head: { ref: 'feature', sha: SHA, repo: { full_name: `${OWNER}/${REPO}` } },
        base: { ref: 'dotnet' },
        ...overrides,
    };
}

function job(id, name, conclusion, failedStep) {
    return {
        id,
        name,
        conclusion,
        html_url: `https://github.com/${OWNER}/${REPO}/actions/runs/${RUN_ID}/job/${id}`,
        steps: [
            { name: 'Checkout', conclusion: 'success' },
            { name: failedStep ?? 'Run tests', conclusion: conclusion === 'success' ? 'success' : 'failure' },
        ],
    };
}

const UNIT_JOB = job(1, 'Unit Tests (10.0.x)', 'success');
const INTEGRATION_JOB = job(2, 'Integration Tests (10.0.x)', 'failure', 'Run integration tests');

function requestComment(sha, workflowId = WORKFLOW_ID) {
    return { body: `@copilot fix it\n<!-- copilot-ci-autofix:request workflow=${workflowId} sha=${sha} -->` };
}

function ciFailureIssue(number, workflowId = WORKFLOW_ID) {
    return {
        number,
        state: 'open',
        labels: [{ name: 'ci-failure' }],
        body: `Failing\n<!-- copilot-ci-autofix:issue workflow=${workflowId} -->`,
        html_url: `https://github.com/${OWNER}/${REPO}/issues/${number}`,
    };
}

function setup({
    run = pullRequestRun(),
    pullRequests = [pullRequest()],
    comments = {},
    issues = [],
    labels = ['automated'],
    jobsByRun = { [RUN_ID]: [UNIT_JOB, INTEGRATION_JOB] },
    logs = { 2: DOTNET_LOG },
    baseRuns = [],
    branchRuns = [],
    sessions = [],
    assign = async () => ({ data: { assignees: [{ login: 'Copilot' }] } }),
    env = { COPILOT_AUTOFIX_TOKEN: 'user-token' },
    onSleep = () => {},
} = {}) {
    const calls = [];
    const record = (name, params) => calls.push({ name, params });
    const state = {
        pullRequests: new Map(pullRequests.map(item => [item.number, item])),
        comments: new Map(Object.entries(comments).map(([number, list]) => [Number(number), [...list]])),
        issues: [...issues],
        labels: new Set(labels),
    };
    let nextId = 1000;

    const paginate = async (method, params) => {
        const { data } = await method(params);
        return Array.isArray(data) ? data : data.jobs ?? data.workflow_runs;
    };

    const github = {
        rest: {
            actions: {
                listJobsForWorkflowRun: async params => {
                    record('listJobsForWorkflowRun', params);
                    return { data: { jobs: jobsByRun[params.run_id] ?? [] } };
                },
                downloadJobLogsForWorkflowRun: async params => {
                    record('downloadJobLogsForWorkflowRun', params);
                    const log = logs[params.job_id];
                    if (log === undefined) {
                        throw Object.assign(new Error('Not Found'), { status: 404 });
                    }
                    return { data: log };
                },
                listWorkflowRuns: async params => {
                    record('listWorkflowRuns', params);
                    return { data: { workflow_runs: params.event === 'push' ? baseRuns : branchRuns } };
                },
                listWorkflowRunsForRepo: async params => {
                    record('listWorkflowRunsForRepo', params);
                    const working = sessions.length > 0 ? sessions.shift() : false;
                    const session = { status: working ? 'in_progress' : 'completed', event: 'dynamic', path: 'dynamic/copilot-swe-agent/copilot' };
                    return { data: { workflow_runs: [{ status: 'in_progress', event: 'dynamic', path: 'dynamic/pages/pages-build-deployment' }, session] } };
                },
            },
        },
        paginate,
    };

    const userClient = {
        rest: {
            pulls: {
                get: async params => {
                    record('pulls.get', params);
                    return { data: structuredClone(state.pullRequests.get(params.pull_number)) };
                },
                list: async params => {
                    record('pulls.list', params);
                    return { data: [...state.pullRequests.values()].filter(item => item.state === 'open' && `${OWNER}:${item.head.ref}` === params.head) };
                },
            },
            issues: {
                listComments: async params => ({ data: state.comments.get(params.issue_number) ?? [] }),
                createComment: async params => {
                    record('createComment', params);
                    const comment = { id: nextId++, body: params.body, html_url: `https://github.com/${OWNER}/${REPO}/issues/${params.issue_number}#comment` };
                    state.comments.set(params.issue_number, [...(state.comments.get(params.issue_number) ?? []), comment]);
                    return { data: comment };
                },
                listForRepo: async params => ({
                    data: state.issues.filter(issue => issue.state === params.state && issue.labels.some(label => label.name === params.labels)),
                }),
                create: async params => {
                    record('create', params);
                    const number = nextId++;
                    const issue = { ...params, number, state: 'open', labels: params.labels.map(name => ({ name })), html_url: `https://github.com/${OWNER}/${REPO}/issues/${number}` };
                    state.issues.push(issue);
                    return { data: issue };
                },
                update: async params => {
                    record('update', params);
                    const issue = state.issues.find(item => item.number === params.issue_number);
                    issue.state = params.state;
                    return { data: issue };
                },
                addAssignees: async params => {
                    record('addAssignees', params);
                    return assign(params);
                },
                getLabel: async params => {
                    if (!state.labels.has(params.name)) {
                        throw Object.assign(new Error('Not Found'), { status: 404 });
                    }
                    return { data: { name: params.name } };
                },
                createLabel: async params => {
                    record('createLabel', params);
                    state.labels.add(params.name);
                    return { data: params };
                },
            },
        },
        paginate,
    };

    const core = {
        infos: [],
        warnings: [],
        failures: [],
        summaries: [],
        info: message => core.infos.push(message),
        warning: message => core.warnings.push(message),
        setFailed: message => core.failures.push(message),
        summary: {
            addHeading() { return this; },
            addRaw(text) { core.summaries.push(text); return this; },
            async write() { return this; },
        },
    };

    const context = {
        repo: { owner: OWNER, repo: REPO },
        serverUrl: 'https://github.com',
        runId: 999,
        payload: { workflow_run: run, workflow: { name: '.NET Tests' }, repository: { default_branch: 'dotnet' } },
    };

    const sleeps = [];
    const tokens = [];
    const invoke = () => handleCompletedRun({
        github,
        context,
        core,
        getOctokit: token => {
            tokens.push(token);
            return userClient;
        },
        env,
        sleep: async milliseconds => {
            sleeps.push(milliseconds);
            await onSleep(state);
        },
    });

    return {
        invoke,
        state,
        core,
        sleeps,
        tokens,
        called: name => calls.filter(call => call.name === name).map(call => call.params),
        writes: () => calls.filter(call => WRITE_METHODS.has(call.name)),
    };
}

test('runs that neither failed nor passed are ignored', async () => {
    const env = setup({ run: pullRequestRun({ conclusion: 'cancelled' }) });

    assert.equal((await env.invoke()).action, 'skipped');
    assert.deepEqual(env.tokens, []);
});

test('runs from forks are ignored', async () => {
    const env = setup({ run: pullRequestRun({ head_repository: { full_name: 'someone/comics' } }) });

    const result = await env.invoke();

    assert.equal(result.action, 'skipped');
    assert.match(result.message, /fork/);
    assert.deepEqual(env.tokens, []);
});

test('a failure without COPILOT_AUTOFIX_TOKEN only logs a warning', async () => {
    const env = setup({ env: {} });

    const result = await env.invoke();

    assert.equal(result.action, 'skipped');
    assert.equal(env.core.warnings.length, 1);
    assert.match(env.core.warnings[0], /COPILOT_AUTOFIX_TOKEN/);
    assert.deepEqual(env.core.failures, []);
    assert.deepEqual(env.tokens, []);
});

test('a passing default-branch run without COPILOT_AUTOFIX_TOKEN is skipped quietly', async () => {
    const env = setup({ run: defaultBranchRun({ conclusion: 'success' }), env: { COPILOT_AUTOFIX_TOKEN: '  ' } });

    assert.equal((await env.invoke()).action, 'skipped');
    assert.deepEqual(env.core.warnings, []);
});

test('passing pull request runs and runs on other branches are ignored', async () => {
    for (const run of [pullRequestRun({ conclusion: 'success' }), pullRequestRun({ event: 'push', head_branch: 'feature' })]) {
        const env = setup({ run });
        assert.equal((await env.invoke()).action, 'skipped');
        assert.deepEqual(env.tokens, []);
    }
});

test('a failed pull request run asks Copilot to fix it in a comment', async () => {
    const env = setup();

    const result = await env.invoke();

    assert.equal(result.action, 'requested');
    assert.equal(result.attempt, 1);
    assert.deepEqual(env.tokens, ['user-token']);
    const [comment] = env.called('createComment');
    assert.equal(comment.issue_number, 7);
    assert.ok(comment.body.startsWith('@copilot The `.NET Tests` workflow failed on this pull request.'));
    assert.match(comment.body, /do not skip, delete or weaken tests/);
    assert.ok(comment.body.includes(`[\`.NET Tests #42\`](https://github.com/${OWNER}/${REPO}/actions/runs/${RUN_ID}) for commit [\`a1b2c3d\`](https://github.com/${OWNER}/${REPO}/commit/${SHA})`));
    assert.ok(comment.body.includes(`- [\`Integration Tests (10.0.x)\`](${INTEGRATION_JOB.html_url}) — failed step: \`Run integration tests\``));
    assert.ok(!comment.body.includes('Unit Tests'));
    assert.ok(comment.body.includes(`<summary>Log excerpt: Integration Tests (10.0.x)</summary>\n\n\`\`\`text\n${DOTNET_EXCERPT}\n\`\`\``));
    assert.ok(comment.body.includes('Automatic fix request 1 of 3 from [Copilot CI Autofix](https://github.com/octo/comics/actions/runs/999)'));
    assert.ok(comment.body.includes(`\`${OPT_OUT_LABEL}\``));
    assert.ok(comment.body.endsWith(`<!-- copilot-ci-autofix:request workflow=${WORKFLOW_ID} sha=${SHA} -->`));
    assert.deepEqual(env.called('downloadJobLogsForWorkflowRun').map(params => params.job_id), [2]);
    assert.deepEqual(env.called('listJobsForWorkflowRun')[0], { owner: OWNER, repo: REPO, run_id: RUN_ID, filter: 'latest', per_page: 100 });
    assert.deepEqual(env.sleeps, []);
});

test('the pull request is found by branch when the run does not list it', async () => {
    const env = setup({ run: pullRequestRun({ pull_requests: [] }) });

    assert.equal((await env.invoke()).action, 'requested');
    assert.equal(env.called('pulls.list')[0].head, `${OWNER}:feature`);
});

test('pull requests that cannot or should not be fixed are skipped', async () => {
    const cases = [
        [{ run: pullRequestRun({ pull_requests: [] }), pullRequests: [] }, /No open pull request/],
        [{ pullRequests: [pullRequest({ head: { ref: 'feature', sha: NEW_SHA, repo: { full_name: `${OWNER}/${REPO}` } } })] }, /newer than the failed run/],
        [{ pullRequests: [pullRequest({ labels: [{ name: OPT_OUT_LABEL }] })] }, new RegExp(OPT_OUT_LABEL)],
        [{ pullRequests: [pullRequest({ head: { ref: 'feature', sha: SHA, repo: { full_name: 'someone/comics' } } })] }, /fork/],
        [{ pullRequests: [pullRequest({ state: 'closed' })] }, /No open pull request/],
    ];
    for (const [options, reason] of cases) {
        const env = setup(options);
        const result = await env.invoke();
        assert.equal(result.action, 'skipped');
        assert.match(result.message, reason);
        assert.deepEqual(env.writes(), []);
    }
});

test('Copilot is asked only once per commit, whichever workflow asked first', async () => {
    const env = setup({ comments: { 7: [requestComment(SHA, 777)] } });

    const result = await env.invoke();

    assert.equal(result.action, 'skipped');
    assert.match(result.message, /already asked/);
    assert.deepEqual(env.writes(), []);
});

test('after the attempt limit a single note without a Copilot mention is posted', async () => {
    const previous = [requestComment('1111111'), requestComment('2222222'), requestComment('3333333')];
    const env = setup({ comments: { 7: previous } });

    const first = await env.invoke();
    const second = await env.invoke();

    assert.equal(first.action, 'exhausted');
    assert.equal(second.action, 'exhausted');
    const notes = env.called('createComment');
    assert.equal(notes.length, 1);
    assert.ok(!notes[0].body.includes('@copilot'));
    assert.match(notes[0].body, /3 times, which is the limit/);
});

test('COPILOT_AUTOFIX_MAX_ATTEMPTS raises the attempt limit', async () => {
    const previous = [requestComment('1111111'), requestComment('2222222'), requestComment('3333333')];
    const env = setup({ comments: { 7: previous }, env: { COPILOT_AUTOFIX_TOKEN: 'user-token', COPILOT_AUTOFIX_MAX_ATTEMPTS: '5' } });

    const result = await env.invoke();

    assert.equal(result.action, 'requested');
    assert.equal(result.attempt, 4);
    assert.match(env.called('createComment')[0].body, /Automatic fix request 4 of 5/);
});

const failingBaseRun = { id: BASE_RUN_ID, status: 'completed', conclusion: 'failure' };

test('failures that also happen on the base branch are left to the default-branch issue', async () => {
    const env = setup({
        baseRuns: [{ id: 300, status: 'completed', conclusion: 'cancelled' }, failingBaseRun],
        jobsByRun: { [RUN_ID]: [UNIT_JOB, INTEGRATION_JOB], [BASE_RUN_ID]: [INTEGRATION_JOB] },
    });

    const result = await env.invoke();

    assert.equal(result.action, 'skipped');
    assert.match(result.message, /also failing on dotnet/);
    assert.deepEqual(env.called('listWorkflowRuns')[0], {
        owner: OWNER, repo: REPO, workflow_id: WORKFLOW_ID, branch: 'dotnet', event: 'push', status: 'completed', per_page: 10,
    });
    assert.deepEqual(env.writes(), []);
});

test('failures are not treated as inherited when the base branch passes', async () => {
    const env = setup({
        baseRuns: [{ id: BASE_RUN_ID, status: 'completed', conclusion: 'success' }, { id: 201, status: 'completed', conclusion: 'failure' }],
        jobsByRun: { [RUN_ID]: [INTEGRATION_JOB], 201: [INTEGRATION_JOB] },
    });

    assert.equal((await env.invoke()).action, 'requested');
    assert.ok(!env.called('createComment')[0].body.includes('also failing on'));
});

test('jobs that also fail on the base branch are marked in the request', async () => {
    const unitFailure = job(1, 'Unit Tests (10.0.x)', 'failure');
    const env = setup({
        baseRuns: [failingBaseRun],
        jobsByRun: { [RUN_ID]: [unitFailure, INTEGRATION_JOB], [BASE_RUN_ID]: [INTEGRATION_JOB] },
        logs: { 1: DOTNET_LOG, 2: DOTNET_LOG },
    });

    assert.equal((await env.invoke()).action, 'requested');
    const { body } = env.called('createComment')[0];
    assert.ok(body.includes('`Integration Tests (10.0.x)`'));
    assert.ok(body.includes('`Run integration tests` (also failing on `dotnet`, so probably not caused by this pull request)'));
    assert.ok(!body.includes('`Run tests` (also failing'));
});

test("Copilot's pull request for a ci-failure issue is asked to fix the inherited failures", async () => {
    const env = setup({
        pullRequests: [pullRequest({ body: 'Seeds the jobs only once.\n\nFixes #12' })],
        issues: [ciFailureIssue(123), ciFailureIssue(12)],
        baseRuns: [failingBaseRun],
        jobsByRun: { [RUN_ID]: [INTEGRATION_JOB], [BASE_RUN_ID]: [INTEGRATION_JOB] },
    });

    assert.equal((await env.invoke()).action, 'requested');
    assert.ok(!env.called('createComment')[0].body.includes('also failing on'));

    const unrelated = setup({
        pullRequests: [pullRequest({ body: 'Mentions #1234 only' })],
        issues: [ciFailureIssue(123)],
        baseRuns: [failingBaseRun],
        jobsByRun: { [RUN_ID]: [INTEGRATION_JOB], [BASE_RUN_ID]: [INTEGRATION_JOB] },
    });
    assert.equal((await unrelated.invoke()).action, 'skipped');
});

test('a running Copilot session is waited for before asking', async () => {
    const env = setup({ sessions: [true, true, false] });

    const result = await env.invoke();

    assert.equal(result.action, 'requested');
    assert.deepEqual(env.sleeps, [60000, 60000]);
    assert.deepEqual(env.called('listWorkflowRunsForRepo')[0], { owner: OWNER, repo: REPO, branch: 'feature', event: 'dynamic', per_page: 20 });
    assert.equal(env.called('pulls.get').length, 2);
});

test('nothing is asked when the running Copilot session pushed newer commits', async () => {
    const env = setup({
        sessions: [true, false],
        onSleep: state => {
            state.pullRequests.get(7).head.sha = NEW_SHA;
        },
    });

    const result = await env.invoke();

    assert.equal(result.action, 'skipped');
    assert.match(result.message, /newer than the failed run/);
    assert.deepEqual(env.writes(), []);
});

test('waiting for a Copilot session gives up after the time limit', async () => {
    const env = setup({ sessions: Array(100).fill(true) });

    const result = await env.invoke();

    assert.equal(result.action, 'skipped');
    assert.match(result.message, /still working/);
    assert.equal(env.sleeps.length, SESSION_MAX_WAIT_MS / 60000);
    assert.deepEqual(env.writes(), []);
});

test('a request is still posted when a log cannot be downloaded', async () => {
    const env = setup({ logs: {} });

    assert.equal((await env.invoke()).action, 'requested');
    assert.match(env.core.warnings[0], /Could not download the log of job "Integration Tests \(10\.0\.x\)"/);
    assert.ok(!env.called('createComment')[0].body.includes('<details>'));
});

test('a failure on the default branch opens an issue assigned to Copilot', async () => {
    const env = setup({ run: defaultBranchRun(), pullRequests: [] });

    const result = await env.invoke();

    assert.equal(result.action, 'issue-opened');
    assert.equal(result.assigned, true);
    assert.deepEqual(env.called('createLabel').map(label => label.name), ['ci-failure']);
    const [issue] = env.called('create');
    assert.equal(issue.title, 'CI failure: .NET Tests is failing on dotnet');
    assert.deepEqual(issue.labels, ['ci-failure', 'automated']);
    assert.match(issue.body, /open a pull request against `dotnet` that makes the workflow pass again/);
    assert.ok(issue.body.includes(DOTNET_EXCERPT));
    assert.ok(issue.body.endsWith(`<!-- copilot-ci-autofix:issue workflow=${WORKFLOW_ID} -->`));
    assert.deepEqual(env.called('addAssignees'), [{
        owner: OWNER,
        repo: REPO,
        issue_number: result.issue,
        assignees: [COPILOT_ASSIGNEE],
        agent_assignment: { target_repo: `${OWNER}/${REPO}`, base_branch: 'dotnet' },
    }]);
    assert.deepEqual(env.core.failures, []);
});

test('later failures on the default branch are added to the open issue', async () => {
    const env = setup({ run: defaultBranchRun(), issues: [ciFailureIssue(5, 999), ciFailureIssue(12)] });

    const result = await env.invoke();

    assert.equal(result.action, 'issue-updated');
    assert.equal(result.issue, 12);
    const [comment] = env.called('createComment');
    assert.equal(comment.issue_number, 12);
    assert.match(comment.body, /failed again on `dotnet`/);
    assert.equal(env.writes().length, 1);
});

test('the issue is kept but the run fails when Copilot cannot be assigned', async () => {
    const outcomes = [
        async () => { throw new Error('Validation Failed'); },
        async () => ({ data: { assignees: [] } }),
    ];
    for (const assign of outcomes) {
        const env = setup({ run: defaultBranchRun(), assign });

        const result = await env.invoke();

        assert.equal(result.action, 'issue-opened');
        assert.equal(result.assigned, false);
        assert.equal(env.called('create').length, 1);
        assert.equal(env.core.warnings.length, 1);
        assert.equal(env.core.failures.length, 1);
    }
});

test('default-branch runs superseded by a newer run are left to that run', async () => {
    const newerRun = { id: 101, run_number: 43, head_sha: NEW_SHA, event: 'push', status: 'in_progress', html_url: 'https://github.com/octo/comics/actions/runs/101' };
    const ignoredRuns = [
        { id: 102, run_number: 44, head_sha: NEW_SHA, event: 'push', status: 'completed', conclusion: 'cancelled' },
        { id: 103, run_number: 45, head_sha: NEW_SHA, event: 'pull_request', status: 'completed', conclusion: 'success' },
        { id: 104, run_number: 46, head_sha: SHA, event: 'workflow_dispatch', status: 'completed', conclusion: 'success' },
    ];
    const superseded = setup({ run: defaultBranchRun(), branchRuns: [...ignoredRuns, newerRun] });

    const result = await superseded.invoke();

    assert.equal(result.action, 'skipped');
    assert.match(result.message, /actions\/runs\/101/);
    assert.deepEqual(superseded.writes(), []);

    const current = setup({ run: defaultBranchRun(), branchRuns: ignoredRuns });
    assert.equal((await current.invoke()).action, 'issue-opened');
});

test('a passing default-branch run closes the open issue for the workflow', async () => {
    const env = setup({ run: defaultBranchRun({ conclusion: 'success' }), issues: [ciFailureIssue(12)] });

    const result = await env.invoke();

    assert.equal(result.action, 'issue-closed');
    assert.match(env.called('createComment')[0].body, /passes again on `dotnet`/);
    assert.deepEqual(env.called('update'), [{ owner: OWNER, repo: REPO, issue_number: 12, state: 'closed', state_reason: 'completed' }]);
});

test('a passing default-branch run leaves issues alone when there is nothing to close or a newer run decides', async () => {
    const nothingOpen = setup({ run: defaultBranchRun({ conclusion: 'success' }), issues: [ciFailureIssue(12, 999)] });
    assert.equal((await nothingOpen.invoke()).action, 'skipped');
    assert.deepEqual(nothingOpen.writes(), []);

    const newerRun = { id: 101, run_number: 43, head_sha: NEW_SHA, event: 'push', status: 'completed', conclusion: 'failure', html_url: 'https://github.com/octo/comics/actions/runs/101' };
    const superseded = setup({ run: defaultBranchRun({ conclusion: 'success' }), issues: [ciFailureIssue(12)], branchRuns: [newerRun] });
    assert.equal((await superseded.invoke()).action, 'skipped');
    assert.deepEqual(superseded.writes(), []);
});
