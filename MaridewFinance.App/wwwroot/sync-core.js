/*!
 * Maridew Finance - Sync Core
 * ---------------------------------------------------------------------------
 * Pure, side-effect-free helpers shared by the sync implementations:
 *
 *  - row union merge honoring the tombstone (deletion) log
 *  - tombstone record / merge / prune
 *  - payload schema compatibility check
 *
 * Loaded as a plain script by the dashboard and the headless sync page
 * (exposes window.MaridewSyncCore), and required as a CommonJS module by the
 * Node test runner in tests/. No DOM, no IndexedDB, no crypto - so the exact
 * merge code the app ships is also the code the tests exercise.
 * ---------------------------------------------------------------------------
 */
(function (root, factory) {
    var api = factory();
    /* node (tests) */ if (typeof module === 'object' && module.exports) { module.exports = api; }
    /* browser */ root.MaridewSyncCore = api;
})(typeof self !== 'undefined' ? self : this, function () {
    'use strict';

    // Bump when the encrypted payload layout changes in a way older clients
    // cannot safely merge. Clients refuse payloads whose schema is newer
    // than this constant and tell the user to update.
    var SUPPORTED_SCHEMA = 2;

    // Deletions ride inside the payload as {"table": {"id": deletedAtIso}}.
    // Union merges skip tombstoned ids so removed rows stay removed on
    // every device; entries older than the retention window are pruned.
    function recordTombstones(tombs, table, ids, at) {
        var per = tombs[table] || {};
        (ids || []).forEach(function (id) {
            var key = String(id);
            if (!per[key] || String(per[key]) < at) per[key] = at;
        });
        tombs[table] = per;
        return tombs;
    }

    function mergeTombstones(base, incoming) {
        Object.keys(incoming || {}).forEach(function (table) {
            var per = base[table] || {};
            Object.keys(incoming[table]).forEach(function (id) {
                var at = incoming[table][id];
                if (!per[id] || String(per[id]) < String(at)) per[id] = at;
            });
            base[table] = per;
        });
        return base;
    }

    function pruneTombstones(tombs, maxAgeDays) {
        var cutoff = Date.now() - maxAgeDays * 86400000;
        Object.keys(tombs).forEach(function (table) {
            var per = tombs[table];
            Object.keys(per).forEach(function (id) {
                var t = Date.parse(per[id]);
                if (isNaN(t) || t < cutoff) delete per[id];
            });
            if (!Object.keys(per).length) delete tombs[table];
        });
        return tombs;
    }

    function tombstonedIds(tombs, table) {
        var per = tombs[table] || {};
        return Object.keys(per).map(Number);
    }

    // Union of local + cloud rows by id, honoring the deletion log: rows
    // whose id is tombstoned are dropped from BOTH sides (a deleted row stays
    // deleted; the cloud copy wins conflicts for everything else).
    function mergeRowsById(localRows, cloudRows, tombstones) {
        var tombs = tombstones || {};
        var seen = {};
        var out = [];
        (cloudRows || []).forEach(function (r) {
            if (r && r.id != null) {
                var key = String(r.id);
                if (tombs[key]) return;              // deleted: never resurrect
                seen[key] = true;
                out.push(r);
            }
        });
        (localRows || []).forEach(function (r) {
            if (r && r.id != null && !seen[String(r.id)]) {
                if (tombs[String(r.id)]) return;     // locally tombstoned by another device: drop
                out.push(r);
            }
        });
        return out;
    }

    // Returns null when the payload can be merged, or a human-readable
    // error when it was written by a newer schema. Payloads from before
    // schema versioning carry no field and are treated as schema 1.
    function checkSchema(payload) {
        var schema = payload && typeof payload.schema === 'number' ? payload.schema : 1;
        if (schema > SUPPORTED_SCHEMA) {
            return 'This data was written by a newer version of Maridew Finance (schema ' +
                schema + '). Update this app before syncing.';
        }
        return null;
    }

    // The sync worker rejects uploads above 4 MB; warn clients well before.
    var BLOB_SOFT_LIMIT_BYTES = 3_000_000;

    return {
        SUPPORTED_SCHEMA: SUPPORTED_SCHEMA,
        BLOB_SOFT_LIMIT_BYTES: BLOB_SOFT_LIMIT_BYTES,
        recordTombstones: recordTombstones,
        mergeTombstones: mergeTombstones,
        pruneTombstones: pruneTombstones,
        tombstonedIds: tombstonedIds,
        mergeRowsById: mergeRowsById,
        checkSchema: checkSchema
    };
});
