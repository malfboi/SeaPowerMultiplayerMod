// Server-rendered overview of the Analytics Engine data.
//
// Lives on the worker rather than in a static page because the Analytics Engine
// SQL API needs an account API token, and a token in a page you can open is a
// token anyone can take. Everything here runs server-side; the browser only
// receives HTML.
//
// No chart library: the SQL result sets are tens of rows, and an inline <svg>
// renders them without a CDN request (which the CSP would refuse anyway) or a
// 300 KB dependency.

const PALETTE = {
  // Validated with the dataviz palette validator, 3 categorical slots, both
  // modes. Light surface throws one WARN: aqua is below 3:1, so every chart
  // that uses it ships direct labels AND a table view (the relief rule).
  light: {
    surface: '#fcfcfb', page: '#f9f9f7', primary: '#0b0b0b', secondary: '#52514e',
    muted: '#898781', grid: '#e1e0d9', axis: '#c3c2b7', border: 'rgba(11,11,11,0.10)',
    s1: '#2a78d6', s2: '#eb6834', s3: '#1baf7a',
  },
  dark: {
    surface: '#1a1a19', page: '#0d0d0d', primary: '#ffffff', secondary: '#c3c2b7',
    muted: '#898781', grid: '#2c2c2a', axis: '#383835', border: 'rgba(255,255,255,0.10)',
    s1: '#3987e5', s2: '#d95926', s3: '#199e70',
  },
};

const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const fmt = (n, d = 0) =>
  n === null || n === undefined || !isFinite(n) ? '—'
    : Number(n).toLocaleString('en-GB', { minimumFractionDigits: d, maximumFractionDigits: d });

// ── Analytics Engine ───────────────────────────────────────────────────────

async function sql(env, query) {
  const res = await fetch(
    `https://api.cloudflare.com/client/v4/accounts/${env.CF_ACCOUNT_ID}/analytics_engine/sql`,
    { method: 'POST', headers: { Authorization: `Bearer ${env.CF_API_TOKEN}` }, body: query });

  const body = await res.text();
  if (!res.ok) throw new Error(`${res.status}: ${body.slice(0, 300)}`);
  try {
    return JSON.parse(body).data ?? [];
  } catch {
    throw new Error(`unparseable response: ${body.slice(0, 300)}`);
  }
}

// Each panel fails on its own. The SQL dialect is a ClickHouse subset and the
// exact function set moves; one unsupported call should cost you that panel and
// tell you why, not blank the whole page.
async function panel(env, query) {
  try {
    return { rows: await sql(env, query), error: null, query };
  } catch (err) {
    return { rows: [], error: err.message, query };
  }
}

// ── Chart primitives ───────────────────────────────────────────────────────

const W = 720, H = 260, PAD = { t: 16, r: 16, b: 34, l: 52 };
const plotW = W - PAD.l - PAD.r;
const plotH = H - PAD.t - PAD.b;

function axes(ticks, xLabels) {
  let g = '';
  for (const t of ticks) {
    g += `<line x1="${PAD.l}" y1="${t.y}" x2="${W - PAD.r}" y2="${t.y}" class="grid"/>`;
    g += `<text x="${PAD.l - 8}" y="${t.y + 4}" class="tick" text-anchor="end">${esc(t.label)}</text>`;
  }
  g += `<line x1="${PAD.l}" y1="${H - PAD.b}" x2="${W - PAD.r}" y2="${H - PAD.b}" class="axis"/>`;
  for (const l of xLabels) {
    g += `<text x="${l.x}" y="${H - PAD.b + 18}" class="tick" text-anchor="middle">${esc(l.label)}</text>`;
  }
  return g;
}

function scaleY(max) {
  const nice = max <= 0 ? 1 : Math.pow(10, Math.floor(Math.log10(max)));
  const step = max / nice <= 2 ? nice / 2 : max / nice <= 5 ? nice : nice * 2;
  const top = Math.ceil(max / step) * step || 1;
  const ticks = [];
  for (let v = 0; v <= top + 1e-9; v += step) {
    ticks.push({ y: PAD.t + plotH - (v / top) * plotH, label: fmt(v, step < 1 ? 1 : 0) });
  }
  return { top, ticks };
}

/**
 * Multi-series line. Categorical color for identity; a lone series gets slot 1
 * and no legend box, because the title already names it.
 */
function lineChart(id, labels, series) {
  if (!labels.length) return `<p class="empty">No data in range.</p>`;

  const max = Math.max(1e-9, ...series.flatMap((s) => s.values.filter((v) => v != null)));
  const { top, ticks } = scaleY(max);
  const x = (i) => PAD.l + (labels.length === 1 ? plotW / 2 : (i / (labels.length - 1)) * plotW);
  const y = (v) => PAD.t + plotH - (v / top) * plotH;

  const every = Math.ceil(labels.length / 7);
  const xLabels = labels.map((l, i) => ({ x: x(i), label: l }))
    .filter((_, i) => i % every === 0 || i === labels.length - 1);

  let marks = '';
  series.forEach((s, si) => {
    const pts = s.values.map((v, i) => (v == null ? null : [x(i), y(v)])).filter(Boolean);
    if (!pts.length) return;
    marks += `<path d="${pts.map((p, i) => `${i ? 'L' : 'M'}${p[0].toFixed(1)} ${p[1].toFixed(1)}`).join(' ')}"
                    fill="none" stroke="var(--s${si + 1})" stroke-width="2"
                    stroke-linecap="round" stroke-linejoin="round"/>`;
    // Direct-label the endpoint rather than every point.
    const last = pts[pts.length - 1];
    marks += `<circle cx="${last[0].toFixed(1)}" cy="${last[1].toFixed(1)}" r="4"
                      fill="var(--s${si + 1})" stroke="var(--surface)" stroke-width="2"/>`;
  });

  const data = JSON.stringify({ labels, series: series.map((s) => ({ name: s.name, values: s.values })) });

  return `
<div class="chart-wrap">
  <svg class="chart" viewBox="0 0 ${W} ${H}" role="img" data-chart='${esc(data)}'>
    ${axes(ticks, xLabels)}
    <line class="crosshair" y1="${PAD.t}" y2="${PAD.t + plotH}" style="display:none"/>
    ${marks}
    <rect class="hit" x="${PAD.l}" y="${PAD.t}" width="${plotW}" height="${plotH}" fill="transparent"/>
  </svg>
  <div class="tip" hidden></div>
</div>
${series.length > 1 ? legend(series) : ''}
${tableView(id, labels, series)}`;
}

/** Single-measure comparison across nominal categories: one hue for every bar. */
function barChart(id, rows, unit) {
  // A null dimension would otherwise reach the axis as the string "undefined".
  rows = rows.filter((r) => r.label !== '' && r.label != null && isFinite(r.value));
  if (!rows.length) return `<p class="empty">No data in range.</p>`;

  const max = Math.max(1e-9, ...rows.map((r) => r.value));
  const { top, ticks } = scaleY(max);
  const band = plotW / rows.length;
  const bw = Math.min(56, band * 0.6);

  let marks = '';
  rows.forEach((r, i) => {
    const cx = PAD.l + band * (i + 0.5);
    const h = Math.max(0, (r.value / top) * plotH);
    const yTop = PAD.t + plotH - h;
    const rad = Math.min(4, h);
    // Rounded data-end only; the baseline end stays square.
    marks += `<path class="bar" d="M${cx - bw / 2} ${PAD.t + plotH}
                 V${yTop + rad} q0 ${-rad} ${rad} ${-rad}
                 h${bw - rad * 2} q${rad} 0 ${rad} ${rad}
                 V${PAD.t + plotH} Z" fill="var(--s1)">
                 <title>${esc(r.label)}: ${fmt(r.value, 1)}${esc(unit)}</title></path>`;
    marks += `<text x="${cx}" y="${yTop - 6}" class="datalabel" text-anchor="middle">${fmt(r.value, 1)}</text>`;
  });

  const xLabels = rows.map((r, i) => ({ x: PAD.l + band * (i + 0.5), label: r.label }));

  return `
<div class="chart-wrap">
  <svg class="chart" viewBox="0 0 ${W} ${H}" role="img">
    ${axes(ticks, xLabels)}
    ${marks}
  </svg>
</div>
${tableView(id, rows.map((r) => r.label), [{ name: unit || 'Value', values: rows.map((r) => r.value) }])}`;
}

function legend(series) {
  return `<ul class="legend">${series.map((s, i) =>
    `<li><span class="swatch" style="background:var(--s${i + 1})"></span>${esc(s.name)}</li>`).join('')}</ul>`;
}

/** Every chart has a table twin - tooltips enhance, they never gate a value. */
function tableView(id, labels, series) {
  const head = `<tr><th></th>${series.map((s) => `<th>${esc(s.name)}</th>`).join('')}</tr>`;
  const body = labels.map((l, i) =>
    `<tr><th scope="row">${esc(l)}</th>${series.map((s) =>
      `<td>${s.values[i] == null ? '—' : fmt(s.values[i], 1)}</td>`).join('')}</tr>`).join('');
  return `<details class="tv"><summary>Table view</summary>
    <table><thead>${head}</thead><tbody>${body}</tbody></table></details>`;
}

function tile(label, value, sub) {
  return `<div class="tile"><div class="tl">${esc(label)}</div>
    <div class="tv-num">${esc(value)}</div>
    <div class="ts">${esc(sub || '')}</div></div>`;
}

function card(title, note, inner, p) {
  const err = p?.error
    ? `<div class="err"><strong>Query failed.</strong> ${esc(p.error)}
       <details><summary>SQL</summary><pre>${esc(p.query)}</pre></details></div>`
    : '';
  return `<section class="card"><h2>${esc(title)}</h2>
    ${note ? `<p class="note">${esc(note)}</p>` : ''}${err}${inner}</section>`;
}

// ── Raw log access (R2, not Analytics Engine) ──────────────────────────────
//
// Log lines never reach Analytics Engine - it stores numbers and dimensions, so
// there is nowhere to put a message string. Anything textual has to come from
// the gzipped NDJSON blobs, which means listing recent objects and decompressing
// them per page load. R2 Class B operations are the thing that would actually
// cost money, so the GET count is what gets budgeted - not the time span.
//
// Two questions, two sets of objects:
//
//   tail    the newest few batches, every level - "what is happening right now"
//   errors  the newest batches that ingest FLAGGED as containing errors, which
//           reaches back days for the same number of GETs
//
// That flag is the whole trick. Taking the newest N batches and hoping they
// contain the errors is a lottery a busy session always wins: one player
// uploading clean metric batches every minute buries a crash within the hour.
// Listing is cheap and returns custom metadata, so clean batches are skipped
// without ever being fetched.
//
// The union is fetched once - a batch that is both newest and error-bearing is
// decompressed a single time.

const TAIL_BATCHES  = 8;    // newest batches, all levels, for the log tail
const ERROR_BATCHES = 40;   // error-bearing batches - the GET budget
const SCAN_DAYS_MAX = 14;   // R2 retention is 30 days; listing further is free but pointless
const FETCH_CONC    = 6;    // decompressed batches held at once
const MAX_GROUPS    = 60;

async function collectLogs(env, days) {
  const dayKey = (n) => new Date(Date.now() - n * 86400000).toISOString().slice(0, 10);
  const scanDays = Math.max(2, Math.min(SCAN_DAYS_MAX, days));

  let objects = [];
  let truncated = false;
  for (let n = 0; n < scanDays; n++) {
    const listed = await env.LOGS.list({
      prefix: `raw/${dayKey(n)}/`, limit: 1000, include: ['customMetadata'],
    });
    objects = objects.concat(listed.objects || []);
    // Keys sort lexicographically and install IDs are random, so a truncated
    // page is not "the oldest 1000" - it is an arbitrary 1000. Say so rather
    // than quietly rendering a partial day as if it were the whole day.
    truncated = truncated || !!listed.truncated;
  }

  const empty = { tail: [], groups: [], batches: 0, listed: objects.length, scanDays, truncated };
  if (!objects.length) return empty;

  objects.sort((a, b) => new Date(b.uploaded) - new Date(a.uploaded));

  // `errs` is written by ingest. Objects predating that field have it
  // undefined rather than "0", and are treated as maybes - otherwise the panel
  // would go blank for everything uploaded before this deploy.
  const tail = objects.slice(0, TAIL_BATCHES);
  const errorish = objects
    .filter((o) => o.customMetadata?.errs !== '0')
    .slice(0, ERROR_BATCHES);

  const isTail = new Set(tail.map((o) => o.key));
  const keys = [...new Set([...tail, ...errorish].map((o) => o.key))];

  const tailRecords = [];
  const errorRecords = [];

  for (let i = 0; i < keys.length; i += FETCH_CONC) {
    await Promise.all(keys.slice(i, i + FETCH_CONC).map(async (key) => {
      const recs = await readBatch(env, key);
      // Batches pulled only for their errors are discarded down to those
      // errors. Forty decompressed batches held whole would meet the isolate
      // memory limit long before they finished rendering.
      if (isTail.has(key)) tailRecords.push(...recs);
      for (const r of recs) if (isError(r)) errorRecords.push(r);
    }));
  }

  tailRecords.sort((a, b) => (b.ts || 0) - (a.ts || 0));
  return {
    tail: tailRecords,
    groups: groupErrors(errorRecords),
    batches: keys.length,
    listed: objects.length,
    scanDays,
    truncated,
  };
}

async function readBatch(env, key) {
  const obj = await env.LOGS.get(key);
  if (!obj) return [];

  let text;
  try {
    text = await new Response(obj.body.pipeThrough(new DecompressionStream('gzip'))).text();
  } catch {
    return [];   // a truncated upload should cost one batch, not the panel
  }

  const out = [];
  let hdr = null;
  for (const line of text.split('\n')) {
    if (!line) continue;
    let rec;
    try { rec = JSON.parse(line); } catch { continue; }
    if (rec.t === 'h') { hdr = rec; continue; }
    if (rec.t === 'l' || rec.t === 'x') out.push({ ...rec, h: hdr });
  }
  return out;
}

const isError = (r) => r.t === 'x' || r.lv === 'E' || r.lv === 'F';

// ── Error grouping ─────────────────────────────────────────────────────────
//
// One fault produces one row. Untouched, the panel is forty copies of the same
// NullReference with a different tick number in it, and the second distinct
// fault is off the bottom of the list.
//
// Numbers are what make copies look like different bugs: entity ids, tick
// counts, coordinates, byte totals, elapsed times. Blanking them collapses the
// copies and leaves the sentence that names the fault. The stack's first frame
// joins the key so two unrelated call sites raising the same generic message
// stay apart.

// No trailing \b: a unit suffix is normal in these messages ("after 12.4s"),
// and requiring a boundary after the digits leaves the last one behind, so
// "12.4s" and "0.9s" would stay two different faults.
const NUMS = /\b0x[0-9a-fA-F]+|\d+(?:[.,:]\d+)*/g;

const normalise = (s) => String(s ?? '').replace(NUMS, '#').replace(/\s+/g, ' ').trim();

function firstFrame(stack) {
  for (const line of String(stack ?? '').split('\n')) {
    const t = line.trim();
    if (t) return normalise(t).slice(0, 160);
  }
  return '';
}

function groupErrors(records) {
  const byKey = new Map();

  for (const r of records) {
    const lv = r.t === 'x' ? 'F' : (r.lv || '?');
    const key = `${lv}|${normalise(r.m).slice(0, 240)}|${firstFrame(r.st)}`;

    let g = byKey.get(key);
    if (!g) {
      g = {
        lv, count: 0, first: 0, last: 0, sample: r,
        sessions: new Set(), installs: new Set(), versions: new Set(), wordings: new Set(),
      };
      byKey.set(key, g);
    }

    // `n` is the client-side collapse of an exception that fired every frame
    // (LogRingSink dedupes those before they ever reach the ring). Counting
    // records instead of occurrences would understate those by orders of
    // magnitude - exactly the ones worth finding.
    g.count += Math.max(1, Number(r.n) || 1);
    if (r.ts) {
      if (!g.first || r.ts < g.first) g.first = r.ts;
      // Newest occurrence supplies the displayed text and stack: an old stack
      // for a bug still happening today is the less useful of the two.
      if (r.ts >= g.last) { g.last = r.ts; g.sample = r; }
    }
    if (r.h?.s)  g.sessions.add(r.h.s);
    if (r.h?.i)  g.installs.add(r.h.i);
    if (r.h?.pv) g.versions.add(r.h.pv);
    if (r.m)     g.wordings.add(r.m);
  }

  // Newest first, because "recent errors" is the question this panel answers;
  // the occurrence and install counts are there to say which rows matter.
  return [...byKey.values()].sort((a, b) => b.last - a.last);
}

const LEVEL = {
  F: { name: 'Fatal', color: '#d03b3b' },
  E: { name: 'Error', color: '#d03b3b' },
  W: { name: 'Warn', color: '#fab219' },
  M: { name: 'Msg', color: 'var(--secondary)' },
  I: { name: 'Info', color: 'var(--secondary)' },
  D: { name: 'Debug', color: 'var(--muted)' },
};

const clock = (ms) => !ms || !isFinite(ms) ? '—'
  : new Date(ms).toISOString().replace('T', ' ').slice(5, 19);

/** Severity reads from the chip's text as well as its colour, never colour alone. */
function chip(lv) {
  const l = LEVEL[lv] || { name: lv || '?', color: 'var(--muted)' };
  return `<span class="chip" style="--c:${l.color}">${esc(l.name)}</span>`;
}

function errorGroups(groups) {
  if (!groups.length)
    return `<p class="empty">No errors in the batches scanned. That is the good outcome.</p>`;

  const shown = groups.slice(0, MAX_GROUPS);

  return `<div class="scroll"><table class="log"><thead><tr>
    <th>Last seen (UTC)</th><th>Level</th><th class="n">Times</th><th class="n">Installs</th>
    <th class="n">Sessions</th><th>Versions</th><th>Message</th></tr></thead><tbody>
    ${shown.map((g) => `<tr>
      <td class="mono">${esc(clock(g.last))}</td>
      <td>${chip(g.lv)}</td>
      <td class="n">${fmt(g.count)}</td>
      <td class="n">${fmt(g.installs.size)}</td>
      <td class="n">${fmt(g.sessions.size)}</td>
      <td>${esc([...g.versions].sort().join(', ') || '—')}</td>
      <td class="msg">${esc(g.sample.m)}
        <div class="meta">first seen ${esc(clock(g.first))}${
          g.wordings.size > 1 ? ` · ${g.wordings.size} wordings collapsed` : ''}</div>
        ${g.sample.st
          ? `<details><summary>stack</summary><pre>${esc(g.sample.st)}</pre></details>` : ''}
      </td>
    </tr>`).join('')}
  </tbody></table></div>
  ${groups.length > shown.length
    ? `<p class="empty">${fmt(groups.length - shown.length)} further distinct errors not shown.</p>`
    : ''}`;
}

function logTail(records, limit = 120) {
  const lines = records.slice(0, limit);
  if (!lines.length) return `<p class="empty">No log lines in the batches checked.</p>`;

  return `<div class="scroll tail">${lines.map((r) => `<div class="ln">
    <span class="mono t">${esc(clock(r.ts))}</span>
    ${chip(r.t === 'x' ? 'F' : r.lv)}
    <span class="src">${esc(r.src || '')}</span>
    <span class="msg">${esc(r.m)}</span></div>`).join('')}</div>`;
}

// ── Page ───────────────────────────────────────────────────────────────────

export async function renderDashboard(env, days) {
  const D = Math.max(1, Math.min(90, days || 14));
  const T = `spmp_metrics`;
  const since = `timestamp > NOW() - INTERVAL '${D}' DAY`;

  // Double positions follow the mapping in index.js - see the contract note
  // there. 1 rttA 2 rttP95 3 loss 4 bin 5 bout 6 sfmx 7 fpsA 8 fpsMin 9 hitch
  // 10 mis 11 driftSA 12 driftAA 13 perrSA 14 perrAA 15 jit 16 cad 17 rep
  // 18 misEl 19 err 20 warn
  //
  // _sample_interval is Analytics Engine's sampling weight. Counting rows
  // without it under-reports once a dataset gets busy enough to sample.
  //
  // Mission-time panels exclude double18 = 0 (menu, no mission loaded) and cap
  // at four hours so one stray clock does not flatten the axis.
  const inMission = `AND double18 > 0 AND double18 < 14400`;

  const [kpi, perDay, rttByTransport, errByVersion, byMode,
         errPerDay, errByMission, hitchByMission, perrByMission, logs] = await Promise.all([
    panel(env, `SELECT COUNT(DISTINCT blob2) AS sessions, COUNT(DISTINCT blob1) AS players,
                       avg(double2) AS rtt_p95, avg(double3) AS loss,
                       avg(double7) AS fps, sum(double9 * _sample_interval) AS hitches,
                       sum(double19 * _sample_interval) AS errs
                FROM ${T} WHERE ${since}`),
    panel(env, `SELECT toDate(timestamp) AS d, COUNT(DISTINCT blob2) AS sessions
                FROM ${T} WHERE ${since} GROUP BY d ORDER BY d`),
    panel(env, `SELECT toDate(timestamp) AS d, blob4 AS transport, avg(double2) AS rtt
                FROM ${T} WHERE ${since} AND blob4 != '' GROUP BY d, transport ORDER BY d`),
    panel(env, `SELECT index1 AS version, avg(double13) AS perr
                FROM ${T} WHERE ${since} GROUP BY version ORDER BY version`),
    panel(env, `SELECT blob5 AS mode, COUNT(DISTINCT blob2) AS sessions
                FROM ${T} WHERE ${since} AND blob5 != '' GROUP BY mode ORDER BY sessions DESC`),
    panel(env, `SELECT toDate(timestamp) AS d,
                       sum(double19 * _sample_interval) AS errs,
                       sum(double20 * _sample_interval) AS warns
                FROM ${T} WHERE ${since} GROUP BY d ORDER BY d`),
    panel(env, `SELECT floor(double18 / 300) * 5 AS mins,
                       sum(double19 * _sample_interval) AS errs
                FROM ${T} WHERE ${since} ${inMission} GROUP BY mins ORDER BY mins`),
    // double9 is the hitch count over one 10 s window, so x6 is per minute of
    // play. A rate, not a total: later buckets hold fewer sessions, and a raw
    // sum would read that thinning out as the stutters going away.
    panel(env, `SELECT floor(double18 / 300) * 5 AS mins,
                       avg(double9) * 6 AS mean_pm,
                       max(double9) * 6 AS worst_pm,
                       COUNT(DISTINCT blob2) AS sessions
                FROM ${T} WHERE ${since} ${inMission} GROUP BY mins ORDER BY mins`),
    panel(env, `SELECT floor(double18 / 300) * 5 AS mins,
                       avg(double13) AS perr, avg(double11) AS drift
                FROM ${T} WHERE ${since} ${inMission} GROUP BY mins ORDER BY mins`),
    (async () => {
      try { return { data: await collectLogs(env, D), error: null, query: 'R2 list + get' }; }
      catch (err) {
        return { data: { tail: [], groups: [], batches: 0, listed: 0, scanDays: 0 },
                 error: err.message, query: 'R2 list + get' };
      }
    })(),
  ]);

  const k = kpi.rows[0] || {};
  const L = logs.data;

  // Pivot the transport rows into one series per transport, aligned on date.
  const tDays = [...new Set(rttByTransport.rows.map((r) => r.d))].sort();
  const transports = [...new Set(rttByTransport.rows.map((r) => r.transport))].sort();
  const tSeries = transports.map((name) => ({
    name,
    values: tDays.map((d) => {
      const hit = rttByTransport.rows.find((r) => r.d === d && r.transport === name);
      return hit ? Number(hit.rtt) : null;
    }),
  }));

  const light = PALETTE.light, dark = PALETTE.dark;
  const vars = (p) => Object.entries({
    surface: p.surface, page: p.page, primary: p.primary, secondary: p.secondary,
    muted: p.muted, grid: p.grid, axis: p.axis, border: p.border,
    s1: p.s1, s2: p.s2, s3: p.s3,
  }).map(([k2, v]) => `--${k2}:${v}`).join(';');

  return `<!doctype html><html lang="en"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="robots" content="noindex,nofollow">
<title>Seapower MP · Diagnostics</title>
<style>
:root{color-scheme:light dark;${vars(light)}}
@media (prefers-color-scheme:dark){:root{${vars(dark)}}}
*{box-sizing:border-box}
body{margin:0;padding:24px;background:var(--page);color:var(--primary);
  font:14px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
header{max-width:1120px;margin:0 auto 20px}
h1{font-size:19px;margin:0 0 2px}
.sub{color:var(--secondary);font-size:13px;margin:0}
main{max-width:1120px;margin:0 auto;display:grid;gap:16px}
.kpis{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px}
.tile{background:var(--surface);border:1px solid var(--border);border-radius:10px;padding:14px 16px}
.tl{font-size:12px;color:var(--secondary)}
.tv-num{font-size:30px;line-height:1.15;margin-top:2px}
.ts{font-size:12px;color:var(--muted)}
.card{background:var(--surface);border:1px solid var(--border);border-radius:10px;padding:16px 18px 12px}
h2{font-size:14px;margin:0 0 2px}
.note{margin:0 0 10px;color:var(--secondary);font-size:12.5px}
.chart-wrap{position:relative;overflow-x:auto}
.chart{width:100%;height:auto;display:block;min-width:520px}
.grid{stroke:var(--grid);stroke-width:1}
.axis{stroke:var(--axis);stroke-width:1}
.crosshair{stroke:var(--axis);stroke-width:1}
.tick{fill:var(--muted);font-size:11px;font-variant-numeric:tabular-nums}
.datalabel{fill:var(--secondary);font-size:11px}
.bar:hover{opacity:.82}
.legend{display:flex;flex-wrap:wrap;gap:14px;list-style:none;margin:10px 0 0;padding:0;
  font-size:12.5px;color:var(--secondary)}
.swatch{display:inline-block;width:10px;height:10px;border-radius:2px;margin-right:6px}
.tip{position:absolute;pointer-events:none;background:var(--surface);color:var(--primary);
  border:1px solid var(--border);border-radius:8px;padding:7px 9px;font-size:12px;
  box-shadow:0 4px 14px rgba(0,0,0,.16);white-space:nowrap}
.tv{margin-top:10px}
summary{cursor:pointer;color:var(--secondary);font-size:12.5px}
table{border-collapse:collapse;margin-top:8px;font-size:12.5px;width:100%}
th,td{text-align:right;padding:4px 8px;border-bottom:1px solid var(--grid);
  font-variant-numeric:tabular-nums}
th[scope=row],thead th:first-child{text-align:left}
.scroll{overflow:auto;max-height:420px;border:1px solid var(--border);border-radius:8px}
table.log{margin:0;font-size:12.5px}
table.log th{position:sticky;top:0;background:var(--surface);z-index:1}
table.log td,table.log th{text-align:left;vertical-align:top;white-space:nowrap}
table.log td.n,table.log th.n{text-align:right;font-variant-numeric:tabular-nums}
table.log td.msg{white-space:normal;min-width:340px}
table.log td.msg .meta{color:var(--muted);font-size:11.5px;margin-top:2px}
.mono{font-family:ui-monospace,SFMono-Regular,Menlo,Consolas,monospace;font-size:12px}
.chip{display:inline-block;padding:0 6px;border-radius:999px;font-size:11px;line-height:17px;
  color:var(--c);border:1px solid color-mix(in srgb,var(--c) 45%,transparent);
  background:color-mix(in srgb,var(--c) 12%,transparent)}
.tail{padding:6px 2px;font-size:12.5px}
.ln{display:grid;grid-template-columns:112px 58px 92px 1fr;gap:8px;align-items:baseline;
  padding:2px 10px;border-bottom:1px solid var(--grid)}
.ln:last-child{border-bottom:0}
.ln .t{color:var(--muted)}
.ln .src{color:var(--muted);overflow:hidden;text-overflow:ellipsis}
.ln .msg{white-space:pre-wrap;word-break:break-word}
details pre{white-space:pre-wrap;word-break:break-word;margin:6px 0 0;font-size:11.5px;
  color:var(--secondary)}
@media (max-width:640px){.ln{grid-template-columns:1fr;gap:2px}}
.err{background:rgba(208,59,59,.10);border:1px solid rgba(208,59,59,.35);
  border-radius:8px;padding:10px 12px;margin-bottom:10px;font-size:12.5px}
.err pre{overflow-x:auto;font-size:11.5px;margin:6px 0 0}
.empty{color:var(--muted);font-size:13px;margin:12px 0}
footer{max-width:1120px;margin:18px auto 0;color:var(--muted);font-size:12px}
</style></head><body>
<header>
  <h1>Seapower Multiplayer · Diagnostics</h1>
  <p class="sub">Last ${D} days · opt-in players only ·
     <a href="?days=7">7d</a> · <a href="?days=14">14d</a> · <a href="?days=30">30d</a></p>
</header>
<main>
  <div class="kpis">
    ${tile('Sessions', fmt(k.sessions), `last ${D} days`)}
    ${tile('Players', fmt(k.players), 'distinct install IDs')}
    ${tile('RTT p95', fmt(k.rtt_p95) + ' ms', 'mean of per-snapshot p95')}
    ${tile('Packet loss', fmt(k.loss, 2) + '%', 'LiteNetLib only')}
    ${tile('Frame rate', fmt(k.fps) + ' fps', 'mean')}
    ${tile('Stutters', fmt(k.hitches), 'frames over 100 ms')}
    ${tile('Errors', fmt(k.errs), 'logged at Error or above')}
  </div>

  ${card('Sessions per day', 'Distinct session IDs. Both players in one match count separately.',
    lineChart('sessions', perDay.rows.map((r) => String(r.d).slice(5)),
      [{ name: 'Sessions', values: perDay.rows.map((r) => Number(r.sessions)) }]), perDay)}

  ${card('Connection quality by transport',
    'Mean of the per-snapshot 95th-percentile RTT. Steam relays and direct UDP fail differently, so they are worth reading apart.',
    lineChart('rtt', tDays.map((d) => String(d).slice(5)), tSeries), rttByTransport)}

  ${card('Replica prediction error by version',
    'Mean ship prediction error in metres — the number that responds to stream rate and motion-model changes. Lower is better.',
    barChart('perr', errByVersion.rows.map((r) => ({ label: r.version ?? '', value: Number(r.perr) })), ' m'),
    errByVersion)}

  ${card('Sessions by mode', 'PvP versus co-op split.',
    barChart('mode', byMode.rows.map((r) => ({ label: r.mode ?? '', value: Number(r.sessions) })), ''),
    byMode)}

  ${card('Errors and warnings per day', 'Counted from the log stream, not sampled.',
    lineChart('errday', errPerDay.rows.map((r) => String(r.d).slice(5)), [
      { name: 'Errors', values: errPerDay.rows.map((r) => Number(r.errs)) },
      { name: 'Warnings', values: errPerDay.rows.map((r) => Number(r.warns)) },
    ]), errPerDay)}

  ${card('Errors by time into the mission',
    'Bucketed by mission elapsed, five minutes per bar — so a rise on the right means failures that only appear in long matches, which wall-clock charts hide at time compression.',
    barChart('errmis', errByMission.rows.map((r) => ({
      label: fmt(r.mins) + 'm', value: Number(r.errs) })), ' errors'),
    errByMission)}

  ${card('Stutters by time into the mission',
    'Frames over 100 ms, as a rate per minute of play, bucketed by mission elapsed. The mean is what a typical player feels; the worst window is the ugliest single ten seconds any session recorded. Both climbing to the right is the signature of something accumulating — entity count, a leak, replica churn — rather than a slow PC, which would be flat.',
    lineChart('hitchmis', hitchByMission.rows.map((r) => fmt(r.mins) + 'm'), [
      { name: 'Stutters per minute (mean)', values: hitchByMission.rows.map((r) => Number(r.mean_pm)) },
      { name: 'Worst 10 s window', values: hitchByMission.rows.map((r) => Number(r.worst_pm)) },
    ]), hitchByMission)}

  ${card('Sessions reaching each point in the mission',
    'The denominator for the two charts above. A bucket backed by one session is one player’s bad afternoon, not a trend.',
    barChart('missess', hitchByMission.rows.map((r) => ({
      label: fmt(r.mins) + 'm', value: Number(r.sessions) })), ' sessions'),
    hitchByMission)}

  ${card('Replica accuracy by time into the mission',
    'Mean prediction error and drift in metres against mission elapsed. A curve that climbs is desync accumulating; a flat line is the sync layer holding.',
    lineChart('perrmis', perrByMission.rows.map((r) => fmt(r.mins) + 'm'), [
      { name: 'Prediction error (m)', values: perrByMission.rows.map((r) => Number(r.perr)) },
      { name: 'Drift (m)', values: perrByMission.rows.map((r) => Number(r.drift)) },
    ]), perrByMission)}

  ${card('Recent errors',
    `One row per distinct fault, newest first. Identical errors are collapsed — numbers inside the message are ignored when matching, so the same bug with a different entity id counts once and the "Times" column carries the volume. Scanned the last ${L.scanDays} days: ${fmt(L.listed)} batches listed, ${fmt(L.batches)} fetched (only those ingest flagged as carrying an error, plus the newest few).${
      L.truncated ? ' ⚠ A day exceeded the 1000-object listing page, so some batches were not considered.' : ''}`,
    errorGroups(L.groups), logs)}

  ${card('Recent log lines',
    `The newest ${TAIL_BATCHES} batches only, all levels, newest first — including Debug, which never reaches LogOutput.log on the player machine. Unlike the errors above this is a raw tail, so a chatty session will fill it.`,
    logTail(L.tail), logs)}
</main>
<footer>Analytics Engine retains 90 days. Raw NDJSON lives in R2 for 30 days and carries
per-session counters this page does not show.</footer>
<script>
// Crosshair + nearest-point tooltip for the line charts. Values are also in the
// table view, so this enhances and never gates.
for (const wrap of document.querySelectorAll('.chart-wrap')) {
  const svg = wrap.querySelector('.chart[data-chart]');
  if (!svg) continue;
  const d = JSON.parse(svg.dataset.chart);
  const tip = wrap.querySelector('.tip');
  const cross = svg.querySelector('.crosshair');
  const hit = svg.querySelector('.hit');
  const L = ${PAD.l}, PW = ${plotW};

  const move = (ev) => {
    const box = svg.getBoundingClientRect();
    const sx = (ev.clientX - box.left) / box.width * ${W};
    const n = d.labels.length;
    let i = n < 2 ? 0 : Math.round((sx - L) / PW * (n - 1));
    i = Math.max(0, Math.min(n - 1, i));
    const gx = n < 2 ? L + PW / 2 : L + (i / (n - 1)) * PW;
    cross.setAttribute('x1', gx); cross.setAttribute('x2', gx);
    cross.style.display = '';
    tip.hidden = false;
    tip.innerHTML = '<strong>' + d.labels[i] + '</strong>' +
      d.series.map(s => '<br>' + s.name + ': ' +
        (s.values[i] == null ? '—' : Number(s.values[i]).toFixed(1))).join('');
    const px = gx / ${W} * box.width;
    tip.style.left = Math.min(box.width - tip.offsetWidth - 4, Math.max(0, px + 12)) + 'px';
    tip.style.top = '8px';
  };
  hit.addEventListener('mousemove', move);
  hit.addEventListener('mouseleave', () => { tip.hidden = true; cross.style.display = 'none'; });
}
</script>
</body></html>`;
}
