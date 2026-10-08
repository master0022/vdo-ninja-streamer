// Headless viewer: opens https://b.siobud.com/<key>, samples inbound-rtp video stats 1x/s,
// prints a compact summary of freezes/loss/keyframes. usage: node viewer.js <key> <seconds>
const puppeteer = require('puppeteer-core');
const [key, secs = '60'] = process.argv.slice(2);
(async () => {
  const browser = await puppeteer.launch({
    executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    headless: 'new',
    args: ['--autoplay-policy=no-user-gesture-required', '--no-sandbox'],
  });
  const page = await browser.newPage();
  await page.evaluateOnNewDocument(() => {
    window.__pcs = [];
    const Orig = window.RTCPeerConnection;
    window.RTCPeerConnection = function (...a) { const pc = new Orig(...a); window.__pcs.push(pc); return pc; };
    window.RTCPeerConnection.prototype = Orig.prototype;
  });
  await page.goto(`https://${process.env.HOST||'b.siobud.com'}/${key}`, { waitUntil: 'domcontentloaded' });
  const rows = [];
  const t0 = Date.now();
  while ((Date.now() - t0) / 1000 < Number(secs)) {
    await new Promise(r => setTimeout(r, 1000));
    const s = await page.evaluate(async () => {
      for (const pc of window.__pcs) {
        const st = await pc.getStats();
        for (const r of st.values()) if (r.type === 'inbound-rtp' && r.kind === 'video')
          return { fd: r.framesDecoded || 0, fdr: r.framesDropped || 0, fz: r.freezeCount || 0, fzd: r.totalFreezesDuration || 0,
                   pl: r.packetsLost || 0, pr: r.packetsReceived || 0, nack: r.nackCount || 0, pli: r.pliCount || 0,
                   kf: r.keyFramesDecoded || 0, w: r.frameWidth, h: r.frameHeight, jb: r.jitterBufferDelay / Math.max(1, r.jitterBufferEmittedCount) };
      }
      return null;
    });
    if (s) rows.push({ t: Math.round((Date.now() - t0) / 1000), ...s });
  }
  let prev = null; const out = [];
  for (const r of rows) {
    if (prev) out.push(`t=${r.t} fps=${r.fd - prev.fd} lost+${r.pl - prev.pl} nack+${r.nack - prev.nack} pli+${r.pli - prev.pli} kf+${r.kf - prev.kf} freezes+${r.fz - prev.fz} frozen+${(r.fzd - prev.fzd).toFixed(2)}s ${r.w}x${r.h} jb=${(r.jb * 1000).toFixed(0)}ms`);
    prev = r;
  }
  const last = rows[rows.length - 1];
  if (process.env.VERBOSE) console.log(out.join('\n'));
  console.log(last ? `SUMMARY samples=${rows.length} freezes=${last.fz} frozenTotal=${last.fzd.toFixed(1)}s lostPkts=${last.pl} nack=${last.nack} pli=${last.pli} keyframes=${last.kf} dropped=${last.fdr}` : 'NO VIDEO RECEIVED');
  await browser.close();
})();
