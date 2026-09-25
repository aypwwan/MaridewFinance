// Syntax gate for every script the app ships: parse-checks the standalone
// JS files AND every inline <script> block in the HTML pages. A single
// syntax error anywhere kills the whole dashboard or the headless sync
// bridge (silent 'no-bridge' on Android), so it must fail the build.
// Run with: node tests/parse-check.js   (no dependencies)
'use strict';

const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..');
const wwwroot = path.join(root, 'MaridewFinance.App', 'wwwroot');

const standalone = ['web-bridge.js', 'sync-core.js'];
const pages = ['index.html', 'sync.html'];

let errors = 0;
function check(name, code) {
    try {
        // `new Function` parses classic scripts only, so ES module syntax
        // (the sync worker's `export default`) is converted to a plain
        // declaration first. Everything else must parse exactly as shipped.
        const asScript = code.replace(/^\s*export\s+default\s+/m, 'const __export_default = ')
                             .replace(/^\s*export\s+\{[^}]*\}\s*;?\s*$/gm, '');
        new Function(asScript);   // throws on syntax errors (any ES version V8 accepts)
        console.log('ok   ' + name);
    } catch (e) {
        errors++;
        console.error('FAIL ' + name + ': ' + (e && e.message || e));
    }
}

standalone.forEach(f => {
    const p = path.join(wwwroot, f);
    if (!fs.existsSync(p)) { errors++; console.error('FAIL missing file: ' + f); return; }
    check(f, fs.readFileSync(p, 'utf8'));
});

pages.forEach(f => {
    const p = path.join(wwwroot, f);
    if (!fs.existsSync(p)) { errors++; console.error('FAIL missing page: ' + f); return; }
    const html = fs.readFileSync(p, 'utf8');
    const re = /<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi;
    let m, i = 0;
    while ((m = re.exec(html)) !== null) {
        i++;
        check(f + ' inline #' + i, m[1]);
    }
    if (i === 0) console.log('warn ' + f + ': no inline scripts found (unexpected)');
});

// The worker ships separately; parse it too.
const worker = path.join(root, 'sync-worker', 'worker.js');
if (fs.existsSync(worker)) check('sync-worker/worker.js', fs.readFileSync(worker, 'utf8'));

if (errors) {
    console.error('\nparse-check: ' + errors + ' file(s) failed');
    process.exit(1);
}
console.log('\nparse-check: all scripts parse');
