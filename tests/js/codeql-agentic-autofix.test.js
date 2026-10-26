'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const autofix = require('../../.github/scripts/codeql-agentic-autofix.js');

const WORKFLOW = '.github/workflows/codeql-analysis.yml';
const OPTIONS = { minSeverity: 'medium', maxInFlight: 5, dryRun: false, workflowPath: WORKFLOW };
const TOKEN = 'github_pat_test_token';

function makeAlert(number, {
    rule = `rule/${number}`,
    severity = 'high',
    assignees = [],
    analysisKey = `${WORKFLOW}:analyze-dotnet`,
    classifications = [],
    state = 'open'
} = {}) {
    return {
        number,
        state,
        html_url: `https://github.com/octo/comics/security/code-scanning/${number}`,
        rule: { id: rule, name: rule, severity: 'warning', security_severity_level: severity },
        assignees: assignees.map(login => ({ login, type: login === 'Copilot' ? 'Bot' : 'User' })),
        most_recent_instance: {
            analysis_key: analysisKey,
            classifications,
            location: { path: 'src/app.js', start_line: number }
        }
    };
}

const numbers = alerts => alerts.map(alert => alert.number);

function fakeCore() {
    const log = { info: [], warning: [], failed: [], summary: '' };
    return {
        log,
        info: message => log.info.push(message),
        warning: message => log.warning.push(message),
        setFailed: message => log.failed.push(message),
        summary: {
            addRaw(markdown) { log.summary += markdown; return this; },
            async write() { return this; }
        }
    };
}

// Stands in for the client getOctokit(CODEQL_AUTOFIX_TOKEN) returns. `onPatch`
// answers each assignment; the alert's re-read returns `current` or the alert.
function fakeClient({ onPatch = ({ alert }) => ({ ...alert, assignees: [{ login: 'Copilot', type: 'Bot' }] }), current = {} } = {}) {
    const requests = [];
    const byNumber = new Map();
    return {
        requests,
        byNumber,
        async request(route, params) {
            requests.push({ route, params });
            const alert = current[params.alert_number] || byNumber.get(params.alert_number);
            if (route === 'GET /repos/{owner}/{repo}/code-scanning/alerts/{alert_number}') return { data: alert };
            if (route === 'PATCH /repos/{owner}/{repo}/code-scanning/alerts/{alert_number}') return { data: onPatch({ alert, params }) };
            throw new Error(`Unexpected request ${route}`);
        }
    };
}

async function runWith(alerts, { env = {}, client = fakeClient() } = {}) {
    for (const alert of alerts) client.byNumber.set(alert.number, alert);
    const core = fakeCore();
    const listCalls = [];
    const tokens = [];
    const github = {
        async paginate(route, params) {
            listCalls.push({ route, params });
            return alerts;
        },
        async request() { throw new Error('Assignments must use the autofix token, not GITHUB_TOKEN.'); }
    };
    const getOctokit = token => { tokens.push(token); return client; };
    const result = await autofix.run({
        github,
        context: { repo: { owner: 'octo', repo: 'comics' } },
        core,
        getOctokit,
        env: { CODEQL_AUTOFIX_TOKEN: TOKEN, ...env }
    });
    const patches = client.requests.filter(request => request.route.startsWith('PATCH '));
    return { result, core, listCalls, tokens, patches };
}

test('parseOptions falls back to the defaults for empty inputs', () => {
    const defaults = { minSeverity: 'medium', maxInFlight: 5, dryRun: false, workflowPath: WORKFLOW };
    assert.deepEqual(autofix.parseOptions({}), defaults);
    // workflow_run leaves the workflow_dispatch inputs empty.
    assert.deepEqual(autofix.parseOptions({ MIN_SEVERITY: '', MAX_IN_FLIGHT: '', DRY_RUN: '', CODEQL_WORKFLOW_PATH: ' ' }), defaults);
});

test('parseOptions reads the workflow inputs', () => {
    assert.deepEqual(
        autofix.parseOptions({ MIN_SEVERITY: ' High ', MAX_IN_FLIGHT: '0', DRY_RUN: 'TRUE', CODEQL_WORKFLOW_PATH: '.github/workflows/scan.yml' }),
        { minSeverity: 'high', maxInFlight: 0, dryRun: true, workflowPath: '.github/workflows/scan.yml' });
});

test('parseOptions rejects invalid inputs', () => {
    assert.throws(() => autofix.parseOptions({ MIN_SEVERITY: 'severe' }), /MIN_SEVERITY/);
    assert.throws(() => autofix.parseOptions({ MIN_SEVERITY: 'constructor' }), /MIN_SEVERITY/);
    for (const value of ['-1', '2.5', 'many', '1e3']) {
        assert.throws(() => autofix.parseOptions({ MAX_IN_FLIGHT: value }), /MAX_IN_FLIGHT/, value);
    }
    assert.throws(() => autofix.parseOptions({ DRY_RUN: 'yes' }), /DRY_RUN/);
});

test('planAssignments ignores alerts from other CodeQL configurations', () => {
    const stale = { analysisKey: 'dynamic/github-code-scanning/codeql:analyze' };
    const plan = autofix.planAssignments([
        makeAlert(1, { ...stale, rule: 'js/stale', assignees: ['Copilot'] }),
        makeAlert(2, stale),
        makeAlert(3, { analysisKey: '.github/workflows/codeql-analysis.yml.bak:analyze' }),
        makeAlert(4)
    ], OPTIONS);

    assert.deepEqual(numbers(plan.tracked), [4]);
    assert.equal(plan.inFlight.size, 0, 'stale Copilot assignments must not hold a slot');
    assert.deepEqual(numbers(plan.selected), [4]);
});

test('planAssignments skips quality alerts, low severities, alerts assigned to people, closed alerts and generated code', () => {
    const plan = autofix.planAssignments([
        makeAlert(1, { severity: null }),
        makeAlert(2, { severity: 'low' }),
        makeAlert(3, { assignees: ['octocat'] }),
        makeAlert(4, { classifications: ['generated'] }),
        makeAlert(5, { classifications: ['library'] }),
        makeAlert(6, { state: 'fixed' }),
        makeAlert(7, { severity: 'medium', classifications: ['test'] })
    ], OPTIONS);

    assert.deepEqual(numbers(plan.waiting), [7]);
    assert.deepEqual(numbers(plan.selected), [7]);
});

test('planAssignments assigns the most severe alerts first, one per rule', () => {
    const plan = autofix.planAssignments([
        makeAlert(10, { rule: 'actions/unpinned-tag', severity: 'medium' }),
        makeAlert(11, { rule: 'actions/unpinned-tag', severity: 'medium' }),
        makeAlert(12, { rule: 'cs/sql-injection', severity: 'critical' }),
        makeAlert(5, { rule: 'js/xss', severity: 'high' }),
        makeAlert(3, { rule: 'js/xss', severity: 'high' }),
        makeAlert(4, { rule: 'cs/log-forging', severity: 'high' })
    ], OPTIONS);

    assert.deepEqual(numbers(plan.waiting), [12, 3, 4, 5, 10, 11]);
    assert.deepEqual(numbers(plan.selected), [12, 3, 4, 10]);
});

test('planAssignments counts rules Copilot is already working on against the limit', () => {
    const alerts = [
        // Bulk-assigned together: one agent session, so one rule in flight.
        makeAlert(1, { rule: 'actions/unpinned-tag', severity: 'medium', assignees: ['Copilot'] }),
        makeAlert(2, { rule: 'actions/unpinned-tag', severity: 'medium', assignees: ['Copilot'] }),
        makeAlert(3, { rule: 'actions/unpinned-tag', severity: 'medium' }),
        makeAlert(4, { rule: 'cs/path-injection', assignees: ['copilot-swe-agent[bot]'] }),
        // Assigned to Copilot as well as a person: still a Copilot session.
        makeAlert(5, { rule: 'js/xss', severity: null, assignees: ['octocat', 'Copilot'] }),
        makeAlert(6, { rule: 'js/xss' }),
        makeAlert(7, { rule: 'cs/log-forging' }),
        makeAlert(8, { rule: 'js/redos' }),
        makeAlert(9, { rule: 'cs/cleartext-storage' })
    ];

    const plan = autofix.planAssignments(alerts, OPTIONS);
    assert.deepEqual([...plan.inFlight.keys()], ['actions/unpinned-tag', 'cs/path-injection', 'js/xss']);
    assert.deepEqual(numbers(plan.inFlight.get('actions/unpinned-tag')), [1, 2]);
    assert.equal(plan.slots, 2);
    assert.deepEqual(numbers(plan.selected), [7, 8], 'rules already with Copilot wait for their fix');

    const full = autofix.planAssignments(alerts, { ...OPTIONS, maxInFlight: 2 });
    assert.equal(full.slots, 0);
    assert.deepEqual(full.selected, []);
});

test('planAssignments honours the minimum severity and a limit of 0', () => {
    const alerts = [
        makeAlert(1, { severity: 'critical' }),
        makeAlert(2, { severity: 'high' }),
        makeAlert(3, { severity: 'medium' }),
        makeAlert(4, { severity: 'low' })
    ];

    assert.deepEqual(numbers(autofix.planAssignments(alerts, { ...OPTIONS, minSeverity: 'high' }).selected), [1, 2]);
    assert.deepEqual(numbers(autofix.planAssignments(alerts, { ...OPTIONS, minSeverity: 'low' }).selected), [1, 2, 3, 4]);
    assert.deepEqual(numbers(autofix.planAssignments(alerts, { ...OPTIONS, maxInFlight: 1 }).selected), [1]);
    assert.deepEqual(autofix.planAssignments(alerts, { ...OPTIONS, maxInFlight: 0 }).selected, []);
});

test('run lists open CodeQL alerts with the workflow token', async () => {
    const { listCalls } = await runWith([], { env: { DRY_RUN: 'true' } });
    assert.deepEqual(listCalls, [{
        route: 'GET /repos/{owner}/{repo}/code-scanning/alerts',
        params: { owner: 'octo', repo: 'comics', state: 'open', tool_name: 'CodeQL', per_page: 100 }
    }]);
});

test('run only reports what it would assign when the token secret is missing', async () => {
    const { result, core, tokens, patches } = await runWith([makeAlert(1), makeAlert(2)], { env: { CODEQL_AUTOFIX_TOKEN: '' } });

    assert.deepEqual(tokens, [], 'no client is created without a token');
    assert.equal(patches.length, 0);
    assert.match(core.log.warning.join('\n'), /CODEQL_AUTOFIX_TOKEN secret is not set/);
    assert.deepEqual(core.log.failed, []);
    assert.deepEqual(result.results.map(r => r.outcome), ['planned', 'planned']);
    assert.match(core.log.summary, /Dry run: the CODEQL_AUTOFIX_TOKEN secret is not set/);
    assert.match(core.log.summary, /### Would assign/);
    assert.match(core.log.summary, /\[#1\]\(https:\/\/github\.com\/octo\/comics\/security\/code-scanning\/1\)/);
});

test('run assigns nothing in a dry run, even with a token', async () => {
    const { result, core, tokens, patches } = await runWith([makeAlert(1)], { env: { DRY_RUN: 'true' } });

    assert.deepEqual(tokens, []);
    assert.equal(patches.length, 0);
    assert.deepEqual(core.log.warning, []);
    assert.deepEqual(result.results.map(r => r.outcome), ['planned']);
    assert.match(core.log.summary, /Dry run: the dry-run input is set/);
});

test('run assigns the selected alerts to Copilot with the autofix token', async () => {
    const alerts = [
        makeAlert(7, { rule: 'js/xss' }),
        makeAlert(8, { rule: 'js/xss' }),
        makeAlert(9, { rule: 'cs/log-forging', severity: 'medium' })
    ];
    const { result, core, tokens, patches } = await runWith(alerts);

    assert.deepEqual(tokens, [TOKEN]);
    assert.deepEqual(patches.map(patch => patch.params), [
        { owner: 'octo', repo: 'comics', alert_number: 7, assignees: ['copilot-swe-agent[bot]'] },
        { owner: 'octo', repo: 'comics', alert_number: 9, assignees: ['copilot-swe-agent[bot]'] }
    ]);
    assert.deepEqual(result.results.map(r => r.outcome), ['assigned', 'assigned']);
    assert.deepEqual(core.log.failed, []);
    assert.match(core.log.summary, /### Assigned this run/);
    assert.match(core.log.summary, /\| `js\/xss` \| `src\/app\.js:7` \| ✅ Assigned to Copilot \|/);
    assert.ok(!JSON.stringify(core.log).includes(TOKEN), 'the token must never be logged');
});

test('run assigns nothing while Copilot is at the limit', async () => {
    const alerts = [makeAlert(1, { rule: 'js/xss', assignees: ['Copilot'] }), makeAlert(2, { rule: 'cs/log-forging' })];
    const { tokens, patches, core } = await runWith(alerts, { env: { MAX_IN_FLIGHT: '1' } });

    assert.deepEqual(tokens, []);
    assert.equal(patches.length, 0);
    assert.match(core.log.summary, /Copilot is at the limit/);
    assert.match(core.log.summary, /### With Copilot[\s\S]*\| `js\/xss` \| \[#1\]/);
});

test('run skips an alert that someone took after it was listed', async () => {
    const listed = makeAlert(1);
    const client = fakeClient({ current: { 1: { ...listed, assignees: [{ login: 'octocat', type: 'User' }] } } });
    const { result, patches, core } = await runWith([listed, makeAlert(2)], { client });

    assert.deepEqual(result.results.map(r => r.outcome), ['skipped', 'assigned']);
    assert.deepEqual(patches.map(patch => patch.params.alert_number), [2]);
    assert.deepEqual(core.log.failed, []);
});

test('run fails when GitHub does not list Copilot as the assignee', async () => {
    const client = fakeClient({ onPatch: ({ alert }) => ({ ...alert, assignees: [] }) });
    const { result, core } = await runWith([makeAlert(1)], { client });

    assert.deepEqual(result.results.map(r => r.outcome), ['failed']);
    assert.match(result.results[0].message, /Agentic Autofix is enabled/);
    assert.equal(core.log.failed.length, 1);
    assert.match(core.log.failed[0], /Could not assign 1 of 1 alert/);
});

test('run reports API errors with a hint and still assigns the other alerts', async () => {
    const client = fakeClient({
        onPatch: ({ alert }) => {
            if (alert.number === 1) {
                throw Object.assign(new Error('Resource not accessible by personal access token'), { status: 403 });
            }
            return { ...alert, assignees: [{ login: 'Copilot', type: 'Bot' }] };
        }
    });
    const { result, core } = await runWith([makeAlert(1), makeAlert(2)], { client });

    assert.deepEqual(result.results.map(r => r.outcome), ['failed', 'assigned']);
    assert.match(result.results[0].message, /Code scanning alerts: Read and write.*HTTP 403: Resource not accessible/);
    assert.match(core.log.summary, /❌ The CODEQL_AUTOFIX_TOKEN token may not update/);
    assert.match(core.log.failed[0], /Could not assign 1 of 2 alert/);
});

test('renderSummary escapes table cells and explains a paused run', () => {
    const alert = makeAlert(1, { rule: 'odd|rule' });
    const plan = { tracked: [alert], inFlight: new Map(), waiting: [alert], selected: [], slots: 0 };
    const paused = autofix.renderSummary({ options: { ...OPTIONS, maxInFlight: 0 }, dryRunReason: null, openCount: 1, plan, results: [] });
    assert.match(paused, /Assignment is paused/);

    const markdown = autofix.renderSummary({
        options: OPTIONS,
        dryRunReason: null,
        openCount: 1,
        plan: { ...plan, selected: [alert], slots: 1 },
        results: [{ alert, outcome: 'failed', message: 'line one\nline | two' }]
    });
    assert.match(markdown, /`odd\\\|rule`/);
    assert.match(markdown, /❌ line one line \\\| two \|$/m);
});
