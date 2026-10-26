'use strict';

// Feeds open CodeQL security alerts on the default branch to Copilot Agentic
// Autofix. Copilot investigates each alert it is assigned, validates a fix and
// opens a draft pull request.
//
// Runs in actions/github-script from .github/workflows/codeql-agentic-autofix.yml.
// Alerts are listed with the workflow's read-only GITHUB_TOKEN. Assignments
// use the CODEQL_AUTOFIX_TOKEN secret, because Copilot only accepts work
// assigned with a user token.
//
// Every assignment starts an agent session that consumes Code Security AI
// credits, so the backlog is fed in slowly: one alert per rule, and at most
// `maxInFlight` rules with Copilot at a time. A rule stays in flight until its
// alert closes (the fix is merged and CodeQL scans the default branch again)
// or Copilot is unassigned from it.
//
// See docs/COPILOT_AUTOFIX.md.

const COPILOT_ASSIGNEE = 'copilot-swe-agent[bot]';

// Alerts list the agent as "Copilot" once it is assigned.
const COPILOT_LOGIN = /^copilot(-swe-agent(\[bot\])?)?$/i;

const SEVERITY_RANK = Object.freeze({ low: 1, medium: 2, high: 3, critical: 4 });

// Code this repository doesn't write by hand, so not worth an agent session.
const SKIPPED_CLASSIFICATIONS = new Set(['generated', 'library']);

const DEFAULTS = Object.freeze({
    minSeverity: 'medium',
    maxInFlight: 5,
    dryRun: false,
    workflowPath: '.github/workflows/codeql-analysis.yml'
});

const TOKEN_SECRET = 'CODEQL_AUTOFIX_TOKEN';
const DOCS = 'docs/COPILOT_AUTOFIX.md';

const LIST_ALERTS = 'GET /repos/{owner}/{repo}/code-scanning/alerts';
const GET_ALERT = 'GET /repos/{owner}/{repo}/code-scanning/alerts/{alert_number}';
const UPDATE_ALERT = 'PATCH /repos/{owner}/{repo}/code-scanning/alerts/{alert_number}';

const ERROR_HINTS = new Map([
    [400, 'GitHub rejected the assignment. Check that Agentic Autofix is enabled for this repository.'],
    [401, `The ${TOKEN_SECRET} secret is invalid or has expired.`],
    [403, `The ${TOKEN_SECRET} token may not update code scanning alerts. It needs "Code scanning alerts: Read and write" on this repository.`],
    [404, `The alert was not found, or the ${TOKEN_SECRET} token cannot access this repository.`]
]);

function text(value) {
    return typeof value === 'string' ? value.trim() : '';
}

/** Reads the workflow inputs. Empty values (as on workflow_run) mean the defaults. */
function parseOptions(env = {}) {
    const minSeverity = text(env.MIN_SEVERITY).toLowerCase() || DEFAULTS.minSeverity;
    if (!Object.hasOwn(SEVERITY_RANK, minSeverity)) {
        throw new Error(`MIN_SEVERITY must be one of ${Object.keys(SEVERITY_RANK).join(', ')}, not "${env.MIN_SEVERITY}".`);
    }

    const maxInFlight = text(env.MAX_IN_FLIGHT);
    if (maxInFlight !== '' && !/^\d+$/.test(maxInFlight)) {
        throw new Error(`MAX_IN_FLIGHT must be a whole number (0 pauses assignment), not "${maxInFlight}".`);
    }

    const dryRun = text(env.DRY_RUN).toLowerCase();
    if (!['', 'true', 'false'].includes(dryRun)) {
        throw new Error(`DRY_RUN must be true or false, not "${env.DRY_RUN}".`);
    }

    return {
        minSeverity,
        maxInFlight: maxInFlight === '' ? DEFAULTS.maxInFlight : Number(maxInFlight),
        dryRun: dryRun === 'true',
        workflowPath: text(env.CODEQL_WORKFLOW_PATH) || DEFAULTS.workflowPath
    };
}

function ruleOf(alert) {
    const rule = alert.rule || {};
    return rule.id || rule.name || `alert-${alert.number}`;
}

function severityOf(alert) {
    const level = alert.rule && alert.rule.security_severity_level;
    return typeof level === 'string' ? level.toLowerCase() : null;
}

/** 0 for quality alerts, which have no security severity. */
function rankOf(alert) {
    const severity = severityOf(alert);
    return severity && Object.hasOwn(SEVERITY_RANK, severity) ? SEVERITY_RANK[severity] : 0;
}

function instanceOf(alert) {
    return alert.most_recent_instance || {};
}

function assigneesOf(alert) {
    return Array.isArray(alert.assignees) ? alert.assignees : [];
}

function isAssignedToCopilot(alert) {
    return assigneesOf(alert).some(user => Boolean(user) && COPILOT_LOGIN.test(user.login || ''));
}

// Alerts from other configurations, such as a disabled default setup, are
// stale: they can't close however the code changes, so they are ignored.
function isFromWorkflow(alert, workflowPath) {
    const key = instanceOf(alert).analysis_key;
    return typeof key === 'string' && key.startsWith(`${workflowPath}:`);
}

function isSkippedCode(alert) {
    const classifications = instanceOf(alert).classifications;
    return Array.isArray(classifications) && classifications.some(name => SKIPPED_CLASSIFICATIONS.has(name));
}

/**
 * Decides which alerts to hand to Copilot.
 *
 * Rules Copilot is already working on are "in flight"; alerts assigned
 * together count once because they share an agent session. Free slots go to
 * the most severe unassigned alerts, one per rule, so repeated findings of a
 * rule wait for the first fix instead of each starting a session.
 */
function planAssignments(alerts, options) {
    const tracked = alerts.filter(alert => alert.state === 'open' && isFromWorkflow(alert, options.workflowPath));

    const inFlight = new Map();
    for (const alert of tracked) {
        if (!isAssignedToCopilot(alert)) continue;
        const rule = ruleOf(alert);
        if (!inFlight.has(rule)) inFlight.set(rule, []);
        inFlight.get(rule).push(alert);
    }

    const minRank = SEVERITY_RANK[options.minSeverity];
    const waiting = tracked
        .filter(alert => rankOf(alert) >= minRank && assigneesOf(alert).length === 0 && !isSkippedCode(alert))
        .sort((a, b) => rankOf(b) - rankOf(a) || a.number - b.number);

    const slots = Math.max(0, options.maxInFlight - inFlight.size);
    const selected = [];
    const selectedRules = new Set();
    for (const alert of waiting) {
        if (selected.length >= slots) break;
        const rule = ruleOf(alert);
        if (inFlight.has(rule) || selectedRules.has(rule)) continue;
        selectedRules.add(rule);
        selected.push(alert);
    }

    return { tracked, inFlight, waiting, selected, slots };
}

function describeError(error) {
    const status = error && error.status;
    const detail = (error && error.message) || String(error);
    const hint = ERROR_HINTS.get(status);
    if (hint) return `${hint} (HTTP ${status}: ${detail})`;
    return status ? `HTTP ${status}: ${detail}` : detail;
}

async function assign(client, repo, alert, core) {
    const params = { ...repo, alert_number: alert.number };
    try {
        // PATCH replaces the assignees, so make sure nobody took the alert since it was listed.
        const { data: current } = await client.request(GET_ALERT, params);
        if (current.state !== 'open' || assigneesOf(current).length > 0) {
            core.info(`Skipped alert #${alert.number}: it changed after it was listed.`);
            return { alert, outcome: 'skipped', message: 'Skipped: changed after it was listed' };
        }

        const { data: updated } = await client.request(UPDATE_ALERT, { ...params, assignees: [COPILOT_ASSIGNEE] });
        if (!isAssignedToCopilot(updated)) {
            const message = 'GitHub did not list Copilot as an assignee. Check that Agentic Autofix is enabled for this repository.';
            core.warning(`Alert #${alert.number}: ${message}`);
            return { alert, outcome: 'failed', message };
        }

        core.info(`Assigned alert #${alert.number} (${ruleOf(alert)}) to Copilot.`);
        return { alert, outcome: 'assigned', message: 'Assigned to Copilot' };
    } catch (error) {
        const message = describeError(error);
        core.warning(`Alert #${alert.number}: ${message}`);
        return { alert, outcome: 'failed', message };
    }
}

function cell(value) {
    // Escape backslashes as well as pipes; otherwise "a\|b" renders as "a|b" (CodeQL js/incomplete-sanitization).
    return String(value ?? '').replace(/[\r\n]+/g, ' ').replace(/[\\|]/g, '\\$&');
}

function link(alert) {
    return alert.html_url ? `[#${alert.number}](${alert.html_url})` : `#${alert.number}`;
}

function location(alert) {
    const where = instanceOf(alert).location || {};
    if (!where.path) return '';
    return `\`${cell(where.path)}${where.start_line ? `:${where.start_line}` : ''}\``;
}

const OUTCOME_ICONS = { assigned: '✅ ', failed: '❌ ' };

/** Markdown for the job summary. */
function renderSummary({ options, dryRunReason, openCount, plan, results }) {
    const lines = ['## CodeQL Agentic Autofix', ''];
    if (dryRunReason) lines.push('> [!NOTE]', `> Dry run: ${dryRunReason}`, '');

    lines.push(
        '| Backlog | Alerts |',
        '| --- | ---: |',
        `| Open CodeQL alerts | ${openCount} |`,
        `| From \`${cell(options.workflowPath)}\` | ${plan.tracked.length} |`,
        `| Unassigned, ${options.minSeverity} security severity or higher | ${plan.waiting.length} |`,
        '',
        `Copilot is working on **${plan.inFlight.size}** rule(s); the limit is **${options.maxInFlight}**.`,
        ''
    );

    if (results.length > 0) {
        lines.push(
            dryRunReason ? '### Would assign' : '### Assigned this run',
            '',
            '| Alert | Severity | Rule | Location | Result |',
            '| --- | --- | --- | --- | --- |',
            ...results.map(({ alert, outcome, message }) =>
                `| ${link(alert)} | ${cell(severityOf(alert))} | \`${cell(ruleOf(alert))}\` | ${location(alert)} | ${OUTCOME_ICONS[outcome] || ''}${cell(message)} |`),
            ''
        );
    } else if (options.maxInFlight === 0) {
        lines.push('Assignment is paused (the limit is 0).', '');
    } else if (plan.slots === 0) {
        lines.push('Nothing assigned: Copilot is at the limit. Merge its pull requests, or dismiss or reassign the alerts below, to free a slot.', '');
    } else {
        lines.push('Nothing to assign.', '');
    }

    if (plan.inFlight.size > 0) {
        lines.push(
            '### With Copilot',
            '',
            'A rule stays here until its alerts close. To free its slot, dismiss the alert or assign it to yourself instead; unassigning Copilot puts it back in the queue.',
            '',
            '| Rule | Alerts |',
            '| --- | --- |',
            ...[...plan.inFlight].map(([rule, alerts]) => `| \`${cell(rule)}\` | ${alerts.map(link).join(', ')} |`),
            ''
        );
    }

    return lines.join('\n');
}

/** Entry point for actions/github-script. */
async function run({ github, context, core, getOctokit, env = process.env }) {
    const options = parseOptions(env);
    const token = text(env[TOKEN_SECRET]);
    const repo = { owner: context.repo.owner, repo: context.repo.repo };

    let dryRunReason = null;
    if (options.dryRun) {
        dryRunReason = 'the dry-run input is set, so no alerts were assigned.';
    } else if (!token) {
        dryRunReason = `the ${TOKEN_SECRET} secret is not set, so no alerts were assigned. See ${DOCS} to finish the setup.`;
        core.warning(`Dry run: ${dryRunReason}`);
    }

    const alerts = await github.paginate(LIST_ALERTS, { ...repo, state: 'open', tool_name: 'CodeQL', per_page: 100 });
    const plan = planAssignments(alerts, options);
    core.info(`${alerts.length} open CodeQL alert(s), ${plan.tracked.length} from ${options.workflowPath}.`);
    core.info(`Copilot is working on ${plan.inFlight.size} rule(s) (limit ${options.maxInFlight}); ${plan.waiting.length} alert(s) are waiting.`);

    const results = [];
    if (dryRunReason) {
        for (const alert of plan.selected) {
            core.info(`Would assign alert #${alert.number} (${ruleOf(alert)}).`);
            results.push({ alert, outcome: 'planned', message: 'Would assign' });
        }
    } else if (plan.selected.length > 0) {
        const client = getOctokit(token);
        for (const alert of plan.selected) {
            results.push(await assign(client, repo, alert, core));
        }
    }

    await core.summary.addRaw(renderSummary({ options, dryRunReason, openCount: alerts.length, plan, results })).write();

    const failures = results.filter(result => result.outcome === 'failed');
    if (failures.length > 0) {
        core.setFailed(`Could not assign ${failures.length} of ${results.length} alert(s) to Copilot. See the job summary and ${DOCS}.`);
    }

    return { dryRunReason, plan, results };
}

module.exports = {
    COPILOT_ASSIGNEE,
    DEFAULTS,
    TOKEN_SECRET,
    parseOptions,
    planAssignments,
    renderSummary,
    run
};
