//Handle feedback form submissions, forward to Discord, and implement rate limiting
//Uses cloudflare workers KV for rate limiting and file size checks for attachments

const COLORS = {
  'Bug Report': 0xE74C3C,
  'Feature Request': 0x3498DB,
  'Feedback': 0x95A5A6,
};

const MAX_FILE_SIZE = 8 * 1024 * 1024; // 8 MB
const RATE_LIMIT_MAX = 10; // Max 10 submissions per IP per hour
const RATE_LIMIT_WINDOW = 3600; // 1 hour in seconds

function base64ToBytes(base64) {
  const binString = atob(base64);
  const bytes = new Uint8Array(binString.length);
  for (let i = 0; i < binString.length; i++) {
    bytes[i] = binString.charCodeAt(i);
  }
  return bytes;
}

export default {
  async fetch(request, env) {
    // CORS preflight
    if (request.method === 'OPTIONS') {
      return new Response(null, {
        headers: {
          'Access-Control-Allow-Origin': '*',
          'Access-Control-Allow-Methods': 'POST',
          'Access-Control-Allow-Headers': 'Content-Type',
        },
      });
    }

    if (request.method !== 'POST') {
      return new Response('Method not allowed', { status: 405 });
    }

    try {
      // Rate limiting
      const ip = request.headers.get('cf-connecting-ip') || 'unknown';
      const rateKey = `rate:${ip}`;
      const rateData = await env.RATE_LIMIT.get(rateKey, 'json');
      const now = Math.floor(Date.now() / 1000);

      if (rateData && rateData.count >= RATE_LIMIT_MAX && (now - rateData.start) < RATE_LIMIT_WINDOW) {
        return new Response('Rate limit exceeded. Try again later.', { status: 429 });
      }

      // Parse JSON body
      const body = await request.json();
      const { category, description, log } = body;

      if (!category || !description) {
        return new Response('Missing category or description', { status: 400 });
      }

      // Check file size if log provided
      let logBytes = null;
      if (log) {
        logBytes = base64ToBytes(log);
        if (logBytes.length > MAX_FILE_SIZE) {
          return new Response(`File too large. Max ${MAX_FILE_SIZE / 1024 / 1024} MB.`, { status: 400 });
        }
      }

      // Build Discord embed
      const embed = {
        title: category,
        description: description.substring(0, 4096),
        color: COLORS[category] || 0x95A5A6,
        timestamp: new Date().toISOString(),
      };

      const webhookPayload = {
        username: 'Seapower MP Feedback',
        embeds: [embed],
      };

      // Forward to Discord
      let discordRes;
      if (logBytes) {
        const discordForm = new FormData();
        discordForm.append('payload_json', JSON.stringify(webhookPayload));
        discordForm.append('files[0]', new Blob([logBytes], { type: 'text/plain' }), 'LogOutput.log');
        discordRes = await fetch(env.DISCORD_WEBHOOK_URL, {
          method: 'POST',
          body: discordForm,
        });
      } else {
        discordRes = await fetch(env.DISCORD_WEBHOOK_URL, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(webhookPayload),
        });
      }

      if (!discordRes.ok) {
        const errText = await discordRes.text();
        return new Response(`Discord error: ${errText}`, { status: 502 });
      }

      // Update rate limit
      if (rateData && (now - rateData.start) < RATE_LIMIT_WINDOW) {
        await env.RATE_LIMIT.put(rateKey, JSON.stringify({ start: rateData.start, count: rateData.count + 1 }), { expirationTtl: RATE_LIMIT_WINDOW });
      } else {
        await env.RATE_LIMIT.put(rateKey, JSON.stringify({ start: now, count: 1 }), { expirationTtl: RATE_LIMIT_WINDOW });
      }

      return new Response('OK', { status: 200 });
    } catch (err) {
      return new Response(`Server error: ${err.message}`, { status: 500 });
    }
  },
};
