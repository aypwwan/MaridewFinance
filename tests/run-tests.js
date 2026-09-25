// Tests for wwwroot/sync-core.js - the exact merge/tombstone/schema code the
// app ships. Run with: node tests/run-tests.js   (no dependencies, no framework)
//
// A tiny assertion harness keeps this runnable on machines with nothing
// installed but node (and the node:assert module, present since forever).
'use strict';

const assert = require('assert');
const Core = require('../MaridewFinance.App/wwwroot/sync-core.js');

let passed = 0;
let failed = 0;
function test(name, fn) {
    try { fn(); passed++; }
    catch (e) { failed++; console.error('FAIL: ' + name + '\n  ' + (e && e.message || e)); }
}

// ---------------------------------------------------------------- mergeRowsById
test('union merge keeps rows unique to each side', () => {
    const local = [{ id: 1 }, { id: 2 }];
    const cloud = [{ id: 2 }, { id: 3 }];
    const out = Core.mergeRowsById(local, cloud, {});
    assert.deepStrictEqual(out.map(r => r.id).sort(), [1, 2, 3]);
});

test('cloud wins conflicts (same id on both sides)', () => {
    const local = [{ id: 1, amount: 100 }];
    const cloud = [{ id: 1, amount: 999 }];
    const out = Core.mergeRowsById(local, cloud, {});
    assert.strictEqual(out.length, 1);
    assert.strictEqual(out[0].amount, 999);
});

test('tombstoned rows are dropped from both sides', () => {
    const local = [{ id: 1 }, { id: 2 }];
    const cloud = [{ id: 2 }, { id: 3 }];
    const out = Core.mergeRowsById(local, cloud, { 2: '2026-01-01T00:00:00Z' });
    assert.deepStrictEqual(out.map(r => r.id).sort(), [1, 3]);
});

test('rows without ids are ignored entirely', () => {
    const out = Core.mergeRowsById([{ nope: 1 }, { id: 5 }], [null, { id: 6 }], {});
    assert.deepStrictEqual(out.map(r => r.id).sort(), [5, 6]);
});

test('empty/undefined inputs produce empty output', () => {
    assert.deepStrictEqual(Core.mergeRowsById([], [], {}), []);
    assert.deepStrictEqual(Core.mergeRowsById(undefined, undefined, undefined), []);
});

// --------------------------------------------------------------- tombstones
test('recordTombstones keeps the latest deletion timestamp', () => {
    const t = {};
    Core.recordTombstones(t, 'transactions', [7], '2026-01-01T00:00:00Z');
    Core.recordTombstones(t, 'transactions', [7], '2026-02-01T00:00:00Z');
    Core.recordTombstones(t, 'transactions', [7], '2025-06-01T00:00:00Z');
    assert.strictEqual(t.transactions[7], '2026-02-01T00:00:00Z');
});

test('mergeTombstones unions tables and ids, latest timestamp wins', () => {
    const base = { transactions: { 1: '2026-01-01T00:00:00Z' } };
    const incoming = { transactions: { 1: '2026-03-01T00:00:00Z', 2: '2026-02-01T00:00:00Z' }, loans: { 9: '2026-01-05T00:00:00Z' } };
    const merged = Core.mergeTombstones(base, incoming);
    assert.strictEqual(merged.transactions[1], '2026-03-01T00:00:00Z');
    assert.strictEqual(merged.transactions[2], '2026-02-01T00:00:00Z');
    assert.strictEqual(merged.loans[9], '2026-01-05T00:00:00Z');
});

test('pruneTombstones drops entries older than the retention window', () => {
    const fresh = new Date(Date.now() - 24 * 3600 * 1000).toISOString();     // 1 day old
    const stale = new Date(Date.now() - 40 * 24 * 3600 * 1000).toISOString(); // 40 days old
    const t = { transactions: { 1: stale, 2: fresh, 3: 'not-a-date' } };
    Core.pruneTombstones(t, 30);
    assert.ok(!t.transactions[1], 'stale tombstone pruned');
    assert.ok(t.transactions[2], 'fresh tombstone kept');
    assert.ok(!t.transactions[3], 'unparseable tombstone pruned');
    assert.strictEqual(t.transactions && Object.keys(t.transactions).length, 1);
});

test('pruneTombstones removes tables left empty', () => {
    const stale = new Date(Date.now() - 90 * 24 * 3600 * 1000).toISOString();
    const t = { budgets: { 1: stale } };
    Core.pruneTombstones(t, 30);
    assert.ok(!t.budgets, 'empty table removed');
});

test('tombstonedIds returns numeric ids', () => {
    assert.deepStrictEqual(Core.tombstonedIds({ transactions: { 3: 'x', 12: 'y' } }, 'transactions'), [3, 12]);
    assert.deepStrictEqual(Core.tombstonedIds({}, 'transactions'), []);
});

// ----------------------------------------------------------------- schema
test('checkSchema accepts unversioned (schema-1) and current payloads', () => {
    assert.strictEqual(Core.checkSchema({ tables: {} }), null);
    assert.strictEqual(Core.checkSchema({ schema: 1 }), null);
    assert.strictEqual(Core.checkSchema({ schema: 2 }), null);
});

test('checkSchema rejects future schemas with an update hint', () => {
    const err = Core.checkSchema({ schema: 3 });
    assert.ok(err && /newer version/.test(err), 'error mentions newer version');
});

test('checkSchema tolerates null/undefined payloads', () => {
    assert.strictEqual(Core.checkSchema(null), null);
    assert.strictEqual(Core.checkSchema(undefined), null);
});

// ----------------------------------------------------------------- exports
test('module exports the expected API surface', () => {
    for (const k of ['SUPPORTED_SCHEMA', 'BLOB_SOFT_LIMIT_BYTES', 'recordTombstones', 'mergeTombstones', 'pruneTombstones', 'tombstonedIds', 'mergeRowsById', 'checkSchema']) {
        assert.ok(Core[k] !== undefined, 'missing export: ' + k);
    }
});

console.log('\nsync-core: ' + passed + ' passed, ' + failed + ' failed');
process.exit(failed ? 1 : 0);
