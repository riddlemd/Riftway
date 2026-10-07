// Recounts docs/parity/*.md summaries and docs/ELECTRON-PARITY.md progress figures from their rows.
// Usage: node tools/recount-parity.js [repo] [--check]   (--check prints differences without writing). ParityCountTests
// fails the unit run when these figures are stale.
const fs = require('fs'), path = require('path');
const repo = process.argv[2] && !process.argv[2].startsWith('--') ? process.argv[2] : path.join(__dirname, '..'), check = process.argv.includes('--check');
const pct = (a, b) => (b ? (100 * a / b) : 0).toFixed(1) + '%';
const cells = l => l.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map(s => s.trim());
const changed = [];

function write(file, before, after) {
  if (before === after) return;
  changed.push(file);
  if (!check) fs.writeFileSync(file, after);
  else {
    const a = before.split('\n'), b = after.split('\n');
    for (let i = 0; i < Math.max(a.length, b.length); i++) if (a[i] !== b[i]) console.log(`${path.basename(file)}:${i + 1}\n  - ${a[i]}\n  + ${b[i]}`);
  }
}

function countChecklist(file) {
  const text = fs.readFileSync(file, 'utf8'), lines = text.split('\n');
  const start = lines.findIndex(l => /^## Checklist/.test(l));
  const sections = [];
  let cur = null, statusCol = -1;
  for (let i = start + 1; i < lines.length; i++) {
    const l = lines[i];
    if (/^## /.test(l)) break;
    if (/^### /.test(l)) { cur = { name: l, done: 0, partial: 0, missing: 0, na: 0, items: 0 }; sections.push(cur); statusCol = -1; continue; }
    if (!cur || !l.startsWith('|')) continue;
    const c = cells(l);
    if (statusCol < 0) { statusCol = c.findIndex(x => x === 'Status'); continue; }
    if (/^-+$/.test(c[0].replace(/[:\s]/g, '')) && c.every(x => /^:?-+:?$/.test(x))) continue;
    const s = c[statusCol];
    cur.items++;
    if (s === 'done') cur.done++; else if (s === 'partial') cur.partial++; else if (s === 'missing') cur.missing++; else if (s === 'n/a') cur.na++;
    else throw new Error(`${file}:${i + 1}: unknown status '${s}'`);
  }
  // Summary rows map to the checklist sections in order; the last is the Total.
  const sumStart = lines.findIndex(l => /^## Summary/.test(l));
  const rows = [];
  for (let i = sumStart; i < start; i++) if (lines[i].startsWith('| ') && !lines[i].startsWith('| Area') && !lines[i].startsWith('|---')) rows.push(i);
  if (rows.length !== sections.length + 1) throw new Error(`${file}: ${rows.length - 1} summary rows for ${sections.length} sections`);
  const tot = { done: 0, partial: 0, missing: 0, na: 0, items: 0 };
  const fmt = (name, s) => {
    const app = s.items - s.na;
    return `| ${name} | ${s.items} | ${s.done} | ${s.partial} | ${s.missing} | ${s.na} | ${pct(s.done, app)} | ${pct(s.done + s.partial / 2, app)} |`;
  };
  sections.forEach((s, k) => {
    for (const key in tot) tot[key] += s[key];
    lines[rows[k]] = fmt(cells(lines[rows[k]])[0], s);
  });
  lines[rows[sections.length]] = fmt('**Total**', tot);
  write(file, text, lines.join('\n'));
  return tot;
}

const bw = countChecklist(path.join(repo, 'docs/parity/browser-window.md'));
const wc = countChecklist(path.join(repo, 'docs/parity/web-contents.md'));

const file = path.join(repo, 'docs/ELECTRON-PARITY.md');
const text = fs.readFileSync(file, 'utf8');
const lines = text.split('\n');
const mod = { d: 0, p: 0, n: 0 }, m2 = { d: 0, p: 0, n: 0 };
let inModules = false;
for (const l of lines) {
  if (/^## /.test(l)) inModules = !/^## (Progress)/.test(l);
  if (!inModules || !l.startsWith('| ') || l.startsWith('| Electron ')) continue;
  const c = cells(l);
  if (c.length < 5) continue;
  const status = c[c.length - 1], wave = c[3];
  const k = /^done/.test(status) ? 'd' : /^partial/.test(status) ? 'p' : 'n';
  mod[k]++;
  // "M2 core, M6 rest" rows are not M2 rows.
  if (wave === "M2") m2[k]++;
}
const tot = o => o.d + o.p + o.n;
let out = lines.map(l => {
  if (l.startsWith('| Module rows below |')) return `| Module rows below | ${mod.d} | ${mod.p} | ${mod.n} | ${tot(mod)} |`;
  if (l.startsWith('| M2 rows |')) return `| M2 rows | ${m2.d} | ${m2.p} | ${m2.n} | ${tot(m2)} |`;
  const row = (t, s) => { const app = s.items - s.na; return `| ${t} | ${app} | ${s.done} | ${s.partial} | ${s.missing} | ${pct(s.done, app)} | ${pct(s.done + s.partial / 2, app)} |`; };
  if (l.startsWith('| [BrowserWindow](parity/browser-window.md)')) return row(cells(l)[0], bw);
  if (l.startsWith('| [webContents](parity/web-contents.md)')) return row(cells(l)[0], wc);
  return l;
}).join('\n');
out = out.replace(/The \d+ rows include/, `The ${tot(mod)} rows include`);
out = out.replace(/(\| `BrowserWindow` \|[^\n]*?)\d+ of \d+ applicable items done \([\d.]+%\)/, (m, p) => `${p}${bw.done} of ${bw.items - bw.na} applicable items done (${pct(bw.done, bw.items - bw.na)})`);
out = out.replace(/(\| `webContents` \|[^\n]*?)\d+ of \d+ applicable items done \([\d.]+%\)/, (m, p) => `${p}${wc.done} of ${wc.items - wc.na} applicable items done (${pct(wc.done, wc.items - wc.na)})`);
write(file, text, out);
console.log(`modules ${JSON.stringify(mod)} m2 ${JSON.stringify(m2)} bw ${JSON.stringify(bw)} wc ${JSON.stringify(wc)}`);
console.log(changed.length ? `changed: ${changed.map(f => path.relative(repo, f)).join(', ')}` : 'no changes');
