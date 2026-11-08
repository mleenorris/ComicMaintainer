#!/usr/bin/env node
'use strict';

// Reads the JSON reports of `dotnet list <project> package --vulnerable --include-transitive
// --format json`, prints every known-vulnerable package as a Markdown table (also added to the
// GitHub Actions job summary), and exits with 1 when a package has a high or critical advisory, or
// when a report says the project could not be checked. Low and moderate advisories are reported
// without failing, so they do not block unrelated changes; Dependabot proposes their updates.
//
// Usage: node scripts/check-vulnerable-packages.js report.json [report.json ...]

const fs = require('node:fs');

const FAILING_SEVERITIES = new Set(['high', 'critical']);

/** Every vulnerable package in one report, one entry per advisory. */
function collectVulnerabilities(report) {
    const found = [];
    for (const project of report.projects || []) {
        for (const framework of project.frameworks || []) {
            const packages = [
                ...(framework.topLevelPackages || []).map((pkg) => ({ pkg, transitive: false })),
                ...(framework.transitivePackages || []).map((pkg) => ({ pkg, transitive: true }))
            ];
            for (const { pkg, transitive } of packages) {
                for (const vulnerability of pkg.vulnerabilities || []) {
                    found.push({
                        project: project.path,
                        framework: framework.framework,
                        id: pkg.id,
                        version: pkg.resolvedVersion,
                        transitive,
                        severity: vulnerability.severity || 'Unknown',
                        advisoryUrl: vulnerability.advisoryurl || ''
                    });
                }
            }
        }
    }
    return found;
}

/** The error-level problems a report lists, such as a project that was not restored. */
function collectProblems(report) {
    const problems = [...(report.problems || [])];
    for (const project of report.projects || []) {
        problems.push(...(project.problems || []).map((problem) => ({ ...problem, project: project.path })));
    }
    return problems.filter((problem) => String(problem.level || '').toLowerCase() === 'error');
}

/** Whether an advisory's severity fails the check. */
function isFailing(vulnerability) {
    return FAILING_SEVERITIES.has(String(vulnerability.severity).toLowerCase());
}

/** The Markdown summary of the vulnerabilities and problems found. */
function formatReport(vulnerabilities, problems) {
    const lines = ['## Vulnerable NuGet packages', ''];
    if (vulnerabilities.length === 0) {
        lines.push('No known-vulnerable packages.');
    } else {
        lines.push('| Severity | Package | Version | Reference | Project | Advisory |');
        lines.push('| --- | --- | --- | --- | --- | --- |');
        const sorted = [...vulnerabilities].sort((a, b) =>
            Number(isFailing(b)) - Number(isFailing(a)) || a.id.localeCompare(b.id));
        for (const v of sorted) {
            const project = String(v.project || '').split(/[\\/]/).pop();
            lines.push(`| ${v.severity} | ${v.id} | ${v.version} | ${v.transitive ? 'transitive' : 'direct'} | ${project} | ${v.advisoryUrl} |`);
        }
    }
    if (problems.length > 0) {
        lines.push('', '### Projects that could not be checked', '');
        for (const problem of problems) {
            lines.push(`- ${problem.project ? `${problem.project}: ` : ''}${problem.text}`);
        }
    }
    return lines.join('\n') + '\n';
}

/** Checks the reports in the given files; returns the process exit code. */
function main(files, { log = console.log, summaryFile = process.env.GITHUB_STEP_SUMMARY } = {}) {
    if (files.length === 0) {
        log('Usage: node scripts/check-vulnerable-packages.js report.json [report.json ...]');
        return 2;
    }

    const vulnerabilities = [];
    const problems = [];
    for (const file of files) {
        const report = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
        vulnerabilities.push(...collectVulnerabilities(report));
        problems.push(...collectProblems(report));
    }

    const markdown = formatReport(vulnerabilities, problems);
    log(markdown);
    if (summaryFile) {
        fs.appendFileSync(summaryFile, markdown);
    }

    const failing = vulnerabilities.filter(isFailing);
    if (failing.length > 0) {
        log(`::error::${failing.length} high or critical advisory(ies) affect the packages above. Update them, or the package that brings them in.`);
        return 1;
    }
    if (problems.length > 0) {
        log('::error::Some projects could not be checked for vulnerable packages.');
        return 1;
    }
    return 0;
}

module.exports = { collectVulnerabilities, collectProblems, isFailing, formatReport, main };

if (require.main === module) {
    process.exitCode = main(process.argv.slice(2));
}
