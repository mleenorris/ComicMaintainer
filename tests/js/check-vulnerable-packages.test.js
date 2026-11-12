'use strict';

// scripts/check-vulnerable-packages.js — the security scan's NuGet gate, which reads the JSON
// reports of `dotnet list package --vulnerable --include-transitive --format json`.

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { collectVulnerabilities, collectProblems, formatReport, main } = require('../../scripts/check-vulnerable-packages');

// The shape `dotnet list package --vulnerable --format json` reports a vulnerable package in.
function report(...packages) {
    return {
        version: 1,
        parameters: '--vulnerable --include-transitive',
        sources: ['https://api.nuget.org/v3/index.json'],
        projects: [{
            path: '/src/src/ComicMaintainer.Core/ComicMaintainer.Core.csproj',
            frameworks: [{
                framework: 'net10.0',
                topLevelPackages: packages.filter((p) => !p.transitive).map(toPackage),
                transitivePackages: packages.filter((p) => p.transitive).map(toPackage)
            }]
        }]
    };
}

function toPackage({ id, severity }) {
    return {
        id,
        requestedVersion: '1.0.0',
        resolvedVersion: '1.0.0',
        vulnerabilities: [{ severity, advisoryurl: `https://github.com/advisories/GHSA-${id}` }]
    };
}

// A project without vulnerable packages is listed without a frameworks entry.
const CLEAN = {
    version: 1,
    parameters: '--vulnerable --include-transitive',
    sources: ['https://api.nuget.org/v3/index.json'],
    projects: [{ path: '/src/src/ComicMaintainer.Core/ComicMaintainer.Core.csproj' }]
};

function run(...reports) {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'vulnerable-packages-'));
    try {
        const files = reports.map((content, index) => {
            const file = path.join(directory, `report-${index}.json`);
            fs.writeFileSync(file, JSON.stringify(content));
            return file;
        });
        const summaryFile = path.join(directory, 'summary.md');
        const lines = [];
        const exitCode = main(files, { log: (line) => lines.push(line), summaryFile });
        return { exitCode, output: lines.join('\n'), summary: fs.readFileSync(summaryFile, 'utf8') };
    } finally {
        fs.rmSync(directory, { recursive: true, force: true });
    }
}

test('a report without vulnerable packages passes', () => {
    const result = run(CLEAN);

    assert.equal(result.exitCode, 0);
    assert.match(result.summary, /No known-vulnerable packages/);
});

for (const severity of ['High', 'Critical']) {
    test(`a ${severity.toLowerCase()} advisory fails the check, also for a transitive package`, () => {
        const result = run(CLEAN, report({ id: 'SharpCompress', severity, transitive: true }));

        assert.equal(result.exitCode, 1);
        assert.match(result.output, /::error::1 high or critical/);
        assert.match(result.summary, new RegExp(`\\| ${severity} \\| SharpCompress \\| 1\\.0\\.0 \\| transitive \\| ComicMaintainer\\.Core\\.csproj \\|`));
    });
}

for (const severity of ['Low', 'Moderate']) {
    test(`a ${severity.toLowerCase()} advisory is reported without failing`, () => {
        const result = run(report({ id: 'MailKit', severity }));

        assert.equal(result.exitCode, 0);
        assert.match(result.summary, new RegExp(`\\| ${severity} \\| MailKit \\| 1\\.0\\.0 \\| direct \\|`));
    });
}

test('every advisory of every framework and reference is collected', () => {
    const vulnerabilities = collectVulnerabilities(report(
        { id: 'A', severity: 'Low' },
        { id: 'B', severity: 'Critical', transitive: true }));

    assert.deepEqual(
        vulnerabilities.map((v) => [v.id, v.severity, v.transitive, v.framework]),
        [['A', 'Low', false, 'net10.0'], ['B', 'Critical', true, 'net10.0']]);
});

test('a project that could not be checked fails the check', () => {
    const notRestored = {
        ...CLEAN,
        problems: [{ level: 'error', text: 'No assets file was found. Please run restore.' }]
    };

    const result = run(notRestored);

    assert.equal(result.exitCode, 1);
    assert.match(result.summary, /No assets file was found/);
    assert.deepEqual(collectProblems({ ...CLEAN, problems: [{ level: 'warning', text: 'w' }] }), []);
});

test('failing advisories are listed first', () => {
    const markdown = formatReport(collectVulnerabilities(report(
        { id: 'Aaa', severity: 'Low' },
        { id: 'Zzz', severity: 'High' })), []);

    assert.ok(markdown.indexOf('Zzz') < markdown.indexOf('Aaa'));
});

test('no report is a usage error', () => {
    const lines = [];

    assert.equal(main([], { log: (line) => lines.push(line), summaryFile: undefined }), 2);
    assert.match(lines.join('\n'), /Usage/);
});
