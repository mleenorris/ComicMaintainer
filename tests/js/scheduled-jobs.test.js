'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { loadScripts } = require('./support/sandbox');

function load() {
    return loadScripts(['js/shared.js', 'js/app/templating.js', 'js/app/scheduled-jobs.js']);
}

// Unwrap an html`` marker (created inside the sandbox) to its markup.
const markup = (sandbox, value) => sandbox.run('htmlToString')(value);

test('parseScheduledJobDate reads offset-less API timestamps as UTC', () => {
    // Outside UTC, so reading the timestamp as local time would shift it.
    const previousTz = process.env.TZ;
    process.env.TZ = 'America/New_York';
    try {
        const parse = load().run('parseScheduledJobDate');
        assert.equal(parse('2026-10-20T16:40:13.9562613').toISOString(), '2026-10-20T16:40:13.956Z');
        assert.equal(parse('2026-10-20T16:40:13').toISOString(), '2026-10-20T16:40:13.000Z');
        assert.equal(parse('2026-10-20T16:40:13.5Z').toISOString(), '2026-10-20T16:40:13.500Z');
        assert.equal(parse('2026-10-20T18:40:13+02:00').toISOString(), '2026-10-20T16:40:13.000Z');
        assert.equal(parse(null), null);
        assert.equal(parse(''), null);
        assert.equal(parse('not a date'), null);
    } finally {
        if (previousTz === undefined) delete process.env.TZ;
        else process.env.TZ = previousTz;
    }
});

test('formatScheduledJobInterval reads minutes in the largest whole unit', () => {
    const format = load().run('formatScheduledJobInterval');
    assert.equal(format(10080), 'Every 7 days');
    assert.equal(format(1440), 'Every day');
    assert.equal(format('120'), 'Every 2 hours');
    assert.equal(format(60), 'Every hour');
    assert.equal(format(90), 'Every 90 minutes');
    assert.equal(format(1), 'Every minute');
    for (const invalid of ['', ' ', 0, -5, 1.5, 'abc']) {
        assert.equal(format(invalid), 'Enter a whole number of minutes (at least 1).', `for ${JSON.stringify(invalid)}`);
    }
});

test('formatScheduledJobDuration picks a readable unit', () => {
    const format = load().run('formatScheduledJobDuration');
    assert.equal(format(250), '250 ms');
    assert.equal(format(4200), '4 s');
    assert.equal(format(125000), '2m 5s');
    assert.equal(format(3780000), '1h 3m');
    assert.equal(format(null), '');
    assert.equal(format(undefined), '');
    assert.equal(format(-1), '');
});

test('status and finding types are shown as words', () => {
    const { run } = load();
    assert.equal(run('scheduledJobStatusLabel')('NeverRun'), 'Never run');
    assert.equal(run('scheduledJobStatusLabel')(null), 'Never run');
    assert.equal(run('scheduledJobStatusLabel')('Success'), 'Success');
    assert.equal(run('humanizeScheduledJobValue')('SeriesMismatch'), 'Series mismatch');
});

test('library-scan passes default on, other options are opt-in', () => {
    const { run } = load();
    const isOn = run('isScheduledJobOptionOn');
    const options = run("SCHEDULED_JOB_OPTIONS['library-scan']");
    const byName = Object.fromEntries(options.map(option => [option.name, option]));

    assert.equal(isOn(byName.retagStale, {}), true);
    assert.equal(isOn(byName.retagStale, { retagStale: false }), false);
    assert.equal(isOn(byName.reprocessModified, {}), false);
    assert.equal(isOn(byName.reprocessModified, { reprocessModified: true }), true);
});

test('saving keeps stored options the card does not show', () => {
    const build = load().run('buildScheduledJobOptionsJson');

    // metadata-backfill has no checkboxes: its maxBatch must survive a save.
    assert.equal(build({ optionsJson: '{"maxBatch":200}' }, {}), '{"maxBatch":200}');
    assert.equal(build({ optionsJson: null }, {}), null);

    const saved = JSON.parse(build(
        { optionsJson: '{"scanForNew":true,"retagStale":true,"futureOption":5}' },
        { scanForNew: false, reprocessModified: true }));
    assert.deepEqual(saved, { scanForNew: false, retagStale: true, futureOption: 5, reprocessModified: true });

    // Unreadable stored options are replaced rather than blocking the save.
    assert.deepEqual(JSON.parse(build({ optionsJson: '{oops' }, { autoCorrect: true })), { autoCorrect: true });
    assert.deepEqual(JSON.parse(build({ optionsJson: '[1,2]' }, { autoCorrect: true })), { autoCorrect: true });
});

test('job cards escape server-provided text', () => {
    const sandbox = load();
    const card = markup(sandbox, sandbox.run('renderScheduledJobCard')({
        jobKey: "odd'key",
        displayName: '<b>Name</b>',
        description: '<img src=x onerror=alert(1)>',
        enabled: true,
        intervalMinutes: 1440,
        lastStatus: 'Failed',
        lastMessage: '<script>alert(1)</script>',
        lastRunUtc: '2026-10-20T16:40:13',
        nextRunUtc: null,
        optionsJson: null
    }));

    assert.ok(!card.includes('<img'), card);
    assert.ok(!card.includes('<script>'), card);
    assert.ok(!card.includes('<b>Name'), card);
    assert.ok(card.includes('&lt;b&gt;Name&lt;/b&gt;'));
    assert.ok(card.includes('scheduled-job-status--failed'));
    assert.ok(card.includes('Every day'));
    assert.ok(card.includes('Not scheduled'));
    // The key reaches the inline handlers only through jsArg.
    assert.ok(!card.includes("saveScheduledJob('odd'key')"), card);
});

test('the library-scan card renders every pass with its default', () => {
    const sandbox = load();
    const card = markup(sandbox, sandbox.run('renderScheduledJobCard')({
        jobKey: 'library-scan',
        displayName: 'Library scan',
        enabled: false,
        intervalMinutes: 60,
        lastStatus: 'NeverRun',
        optionsJson: '{"reconcileDeletions":false}'
    }));

    for (const [name, checked] of [['scanForNew', true], ['reconcileDeletions', false], ['retagStale', true], ['reprocessModified', false]]) {
        const input = card.match(new RegExp(`<input type="checkbox" data-job-option="${name}"[^>]*>`));
        assert.ok(input, `missing ${name}`);
        assert.equal(input[0].includes('checked'), checked, name);
    }
});
