// Opt-in diagnostics ingest for the Seapower Multiplayer mod.
//
// Deliberately a SEPARATE worker from seapower-feedback: different bindings
// (R2 + Analytics Engine vs KV + Discord webhook), a couple of orders of
// magnitude more traffic, and a bug in here must not be able to take down the
// launcher's feedback form or sit in the same script scope as the Discord
// webhook secret.
//
// Raw batches land in R2 as received (already gzipped). Metric lines are also
// written to Analytics Engine so the aggregate questions ("did 0.3.6 reduce
// drift?") can be answered in SQL without downloading blobs.

import { renderDashboard } from './dashboard.js';

const MAX_BODY = 512 * 1024;          // matches AnalyticsUploader.MaxBatchBytes
const VERSION_RE = /^\d+\.\d+\.\d+$/;
const HEX_RE = /^[0-9a-f]{8,64}$/;

// No CORS headers anywhere, on purpose. This is not a browser client, and
// omitting Access-Control-Allow-Origin means a hostile page cannot drive-by
// abuse the endpoint from a visitor's browser.
const noContent = () => new Response(null, { status: 204 });
const fail = (status, extra) => new Response(null, { status, headers: extra || {} });

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);

    if (request.method === 'GET' && url.pathname === '/v1/health') {
      return new Response(JSON.stringify({ ok: true }), {
        headers: { 'Content-Type': 'application/json' },
      });
    }

    if (request.method === 'GET' && url.pathname === '/dash') {
      // Player diagnostics are behind this page, so it is never public. Basic
      // auth over HTTPS is the floor; put Cloudflare Access in front if you want
      // SSO. Absent DASH_PASSWORD the route is disabled rather than open.
      if (!env.DASH_PASSWORD) return fail(404);
      if (!checkBasicAuth(request, env.DASH_PASSWORD)) {
        return new Response('Authentication required', {
          status: 401,
          headers: { 'WWW-Authenticate': 'Basic realm="spmp", charset="UTF-8"' },
        });
      }
      if (!env.CF_ACCOUNT_ID || !env.CF_API_TOKEN) {
        return new Response(
          'Dashboard needs CF_ACCOUNT_ID (var) and CF_API_TOKEN (secret, Account Analytics:Read).',
          { status: 500, headers: { 'Content-Type': 'text/plain' } });
      }
      const html = await renderDashboard(env, parseInt(url.searchParams.get('days') || '14', 10));
      return new Response(html, {
        headers: {
          'Content-Type': 'text/html; charset=utf-8',
          'Cache-Control': 'no-store',
          // The page inlines its own CSS/JS and talks to nothing else.
          'Content-Security-Policy':
            "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; img-src data:",
        },
      });
    }

    if (request.method !== 'POST' || url.pathname !== '/v1/ingest') {
      return fail(405);
    }

    // A way to stop ingestion without shipping a new client build.
    if (env.KILL_SWITCH === '1') return noContent();

    // ── Cheap rejections first, before any CPU is spent on the body ──────
    // There is no shared key. One compiled into a public DLL authenticates
    // nobody, and Steam ticket validation needs a publisher API key for an
    // appid we do not own. What a key was really doing was bouncing scanner
    // traffic, and the request *shape* does that for free: a gzip body posted
    // to this exact path with well-formed X-SPMP-* headers is not a scanner.
    // Everything past that point is bounded by rate limits, not by identity.
    if (!(request.headers.get('content-encoding') || '').includes('gzip')) {
      return fail(400);
    }

    const installId = request.headers.get('x-spmp-install') || '';
    const sessionId = request.headers.get('x-spmp-session') || '';
    const seq       = request.headers.get('x-spmp-seq') || '0';
    const version   = request.headers.get('x-spmp-version') || '';

    // ── Rate limits, BEFORE any rejection that isn't a bare string compare ─
    // The feedback worker's KV limiter only increments after a successful
    // Discord post, so every one of its error paths is free to spam. Ordering
    // matters more than the limit itself: malformed and oversize batches have
    // to cost the caller quota too, or the cheapest attack is the unmetered
    // one. Native rate-limit bindings also avoid KV's 1000-writes/day free
    // ceiling, which a per-request write would exhaust within minutes.
    const rl = await checkLimits(env, request, installId);
    if (rl) return rl;

    if (!HEX_RE.test(installId) || !HEX_RE.test(sessionId) || !VERSION_RE.test(version)) {
      return fail(400);
    }

    const declared = parseInt(request.headers.get('content-length') || '0', 10);
    if (declared > MAX_BODY) return fail(413);

    // ── Body ─────────────────────────────────────────────────────────────
    let raw;
    try {
      raw = await request.arrayBuffer();
    } catch {
      return fail(400);
    }
    // Content-Length can lie; enforce against what actually arrived.
    if (raw.byteLength === 0 || raw.byteLength > MAX_BODY) return fail(413);

    const now = new Date();
    const day = now.toISOString().slice(0, 10);
    const key = `raw/${day}/${installId}/${sessionId}/${String(seq).padStart(6, '0')}.ndjson.gz`;

    // Decompress for the metric extraction only. The blob is stored exactly as
    // received, so a parse failure here never costs us the raw data.
    let text = null;
    try {
      text = await gunzipToText(raw);
    } catch {
      text = null;
    }

    const header = text ? firstJsonLine(text) : null;
    const scan = text ? scanLogLines(text) : { errs: 0, fatals: [] };

    try {
      await env.LOGS.put(key, raw, {
        httpMetadata: { contentType: 'application/x-ndjson', contentEncoding: 'gzip' },
        customMetadata: {
          installId, sessionId, seq: String(seq), version,
          role: header?.role || '', transport: header?.tr || '',
          mode: header?.mode || '', trigger: header?.trig || '',
          country: request.headers.get('cf-ipcountry') || '',
          // Error count so the dashboard can skip clean batches by listing
          // alone. Without it, finding yesterday's crash means decompressing
          // every batch since, which is why that panel could only afford to
          // look at the newest handful.
          errs: String(scan.errs),
        },
      });
    } catch (err) {
      // Storage failure is ours, not the client's. 500 makes the client retry
      // with backoff rather than discard the batch.
      return fail(500);
    }

    let metrics = 0;
    if (env.AE && text && header) {
      try { metrics = writeMetrics(env, request, header, text); }
      catch (err) { console.log(`AE write failed: ${err.message}`); }   // never fail ingest on AE
    }

    // Visible in `wrangler dev` and `wrangler tail`. Without it, "nothing in
    // Analytics Engine" is ambiguous between no binding, no metric lines in the
    // batch, and AE simply not being emulated locally.
    console.log(
      `ingest ${key} bytes=${raw.byteLength} decoded=${text ? 'yes' : 'no'} ` +
      `metricLines=${metrics} errs=${scan.errs} ae=${env.AE ? 'bound' : 'MISSING'} ` +
      `trig=${header?.trig ?? '?'}`
    );

    // Fatal lines are worth a Discord ping so they surface without anyone
    // querying R2. Best-effort, and never blocks the response.
    if (env.DISCORD_WEBHOOK_URL && header && scan.fatals.length) {
      ctx.waitUntil(notifyFatal(env, header, scan.fatals).catch(() => {}));
    }

    return noContent();
  },
};

/** Any username; the password is the secret. Length-independent compare. */
function checkBasicAuth(request, expected) {
  const header = request.headers.get('authorization') || '';
  if (!header.startsWith('Basic ')) return false;
  let decoded;
  try { decoded = atob(header.slice(6)); } catch { return false; }
  const supplied = decoded.slice(decoded.indexOf(':') + 1);
  if (supplied.length !== expected.length) return false;
  let diff = 0;
  for (let i = 0; i < expected.length; i++) diff |= supplied.charCodeAt(i) ^ expected.charCodeAt(i);
  return diff === 0;
}

async function checkLimits(env, request, installId) {
  const ip = request.headers.get('cf-connecting-ip') || 'unknown';
  try {
    if (env.INGEST_LIMIT) {
      const { success } = await env.INGEST_LIMIT.limit({ key: installId });
      if (!success) return fail(429, { 'Retry-After': '120' });
    }
    if (env.IP_LIMIT) {
      // Second, looser limit so one host cannot forge many install ids.
      const { success } = await env.IP_LIMIT.limit({ key: ip });
      if (!success) return fail(429, { 'Retry-After': '120' });
    }
  } catch {
    // A limiter outage must not take ingest down with it.
  }
  return null;
}

async function gunzipToText(arrayBuffer) {
  const stream = new Response(arrayBuffer).body.pipeThrough(new DecompressionStream('gzip'));
  return await new Response(stream).text();
}

function firstJsonLine(text) {
  const nl = text.indexOf('\n');
  try {
    return JSON.parse(nl < 0 ? text : text.slice(0, nl));
  } catch {
    return null;
  }
}

// Analytics Engine limits: 1 index (must be LOW cardinality - it is the
// sampling key), 20 blobs, 20 doubles.
function writeMetrics(env, request, header, text) {
  let written = 0;
  for (const line of text.split('\n')) {
    if (!line.startsWith('{"t":"m"')) continue;

    let m;
    try { m = JSON.parse(line); } catch { continue; }
    written++;

    const rtt = m.rtt || {}, fps = m.fps || {}, drift = m.drift || {}, perr = m.perr || {};

    env.AE.writeDataPoint({
      // Plugin version, NOT installId: the index is the sampling and billing
      // key, and a high-cardinality index destroys the dataset.
      indexes: [String(header.pv || 'unknown')],
      blobs: [
        header.i || '', header.s || '', header.role || '', header.tr || '',
        header.mode || '', String(header.trig || ''), m.hs || '', m.sim || '',
        request.headers.get('cf-ipcountry') || '',
      ],
      // ── The 20 doubles. Order is a CONTRACT: changing it re-labels every
      // historical row, because Analytics Engine stores positions, not names.
      // Only ever change this on a clean dataset, or accept that old rows lie.
      //
      // Not seated, and why - all still present in the R2 blob:
      //   w        constant 10 s, carries nothing
      //   mem      coarse GC number, has never explained a bug
      //   rtt.mx   a single worst packet; p95 is the honest tail
      //   drift.sm same, for drift - the average is the signal
      doubles: [
        num(rtt.a), num(rtt.p95), num(m.loss),
        num(m.bin), num(m.bout), num(m.sfmx),
        num(fps.a), num(fps.mn), num(m.hitch),
        // mis   - shared sync clock (time of day). Both machines agree on it,
        //         so it joins the two sides of one session; wall-clock ts only
        //         matches if both PCs' clocks do.
        num(m.mis),
        num(drift.sa), num(drift.aa),
        num(perr.sa), num(perr.aa),
        num(m.jit), num(m.cad), num(m.rep),
        // misEl - mission elapsed, per-machine baseline. Does not align across
        //         players, but it is the only thing saying how far into a
        //         mission a row sits - underivable from real time at compression.
        num(m.misEl),
        num(m.err), num(m.warn),
      ],
    });
  }
  return written;
}

const num = (v) => (typeof v === 'number' && isFinite(v) ? v : 0);

// One pass over the log lines for both consumers: the error count that rides
// along in R2 custom metadata, and the first few fatals for Discord. Two scans
// over a decompressed megabyte to answer two questions about the same lines
// would be one scan too many.
function scanLogLines(text) {
  let errs = 0;
  const fatals = [];
  for (const line of text.split('\n')) {
    const isExc = line.startsWith('{"t":"x"');
    if (!isExc && !line.startsWith('{"t":"l"')) continue;
    let r;
    try { r = JSON.parse(line); } catch { continue; }

    const fatal = isExc || r.lv === 'F';
    if (fatal || r.lv === 'E') errs++;
    if (fatal && fatals.length < 3) fatals.push(r.m);
  }
  return { errs, fatals };
}

async function notifyFatal(env, header, fatals) {
  await fetch(env.DISCORD_WEBHOOK_URL, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      username: 'Seapower MP Diagnostics',
      embeds: [{
        title: `Fatal in ${header.pv} (${header.role}/${header.tr})`,
        description: fatals.join('\n\n').substring(0, 4000),
        color: 0xE74C3C,
        footer: { text: `install ${String(header.i).slice(0, 8)} · session ${header.s}` },
        timestamp: new Date().toISOString(),
      }],
    }),
  });
}
