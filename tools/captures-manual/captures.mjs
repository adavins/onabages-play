// @ts-check
// Pas 2 i 3: captures del manual amb el perfil «Mostra», en una carpeta de dades a part (ONABAGES_DATA_DIR).
// Els perfils i les dades de l'usuari no es toquen.
//
//   node captures.mjs prepara   → perfil nou «Mostra»: programa «Matins», 3 reproductors, 2 enregistradors, zoom 80 %
//                                 (el nombre de reproductors només canvia en arrencar: per això són dues fases)
//   node captures.mjs captura   → hi posa les llistes i els ajustos de D-139 i fa les captures (JPEG)
//
// Cal que l'aplicació estigui tancada: el motor fa servir el port 47800. No fa sonar ni enregistra res.
import { spawn } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { APP, AUDIO, DATA, SHOTS, sleep } from './comu.mjs';

const DEBUG_PORT = 9481;
const phase = process.argv[2];
if (phase !== 'prepara' && phase !== 'captura') {
  console.log('Ús: node captures.mjs prepara | captura');
  process.exit(2);
}
if (!fs.existsSync(AUDIO)) {
  console.log('Primer: node genera-audios.mjs');
  process.exit(2);
}

// Si ja hi ha un motor al port 47800 (p. ex. l'aplicació de l'usuari oberta), no es fa res.
try {
  const r = await fetch('http://127.0.0.1:47800/health', { signal: AbortSignal.timeout(1000) });
  if (r.ok) { console.log('Hi ha un motor en marxa: tanca Ona Bages Play i torna-ho a provar.'); process.exit(2); }
} catch { /* lliure */ }

if (phase === 'prepara') fs.rmSync(DATA, { recursive: true, force: true }); // sempre des de zero
fs.mkdirSync(DATA, { recursive: true });
fs.mkdirSync(SHOTS, { recursive: true });
fs.rmSync(path.join(DATA, 'engine.json'), { force: true });

// Electron amb la carpeta de dades de mostra i el depurador remot (per clicar i fer captures).
// VS Code (que també és Electron) deixa ELECTRON_RUN_AS_NODE posada: s'ha de treure.
const env = { ...process.env, ONABAGES_DATA_DIR: DATA };
delete env.ELECTRON_RUN_AS_NODE;
const electron = spawn(path.join(APP, 'node_modules', 'electron', 'dist', 'electron.exe'),
  [`--remote-debugging-port=${DEBUG_PORT}`, '.'], { cwd: APP, stdio: 'ignore', env });

/** @type {(() => Promise<void>) | null} */
let finish = null;
process.on('unhandledRejection', async (e) => {
  console.log('ERROR:', /** @type {any} */ (e)?.message ?? e);
  if (finish) await finish(); else { electron.kill(); process.exit(1); }
});

// --- La finestra (Chrome DevTools Protocol) ---
let target;
for (let i = 0; i < 60 && !target; i++) {
  await sleep(1000);
  try {
    const list = await (await fetch(`http://127.0.0.1:${DEBUG_PORT}/json`)).json();
    target = list.find((/** @type {any} */ t) => t.type === 'page' && t.url.includes('index.html'));
  } catch { /* encara no */ }
}
if (!target) { console.log('No s\'ha obert la finestra.'); electron.kill(); process.exit(1); }

const page = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => page.addEventListener('open', r));
let cdpId = 0;
const cdpWaiting = new Map();
/** @type {string[]} */
const consoleErrors = [];
page.addEventListener('message', (e) => {
  const m = JSON.parse(e.data);
  if (m.id && cdpWaiting.has(m.id)) { cdpWaiting.get(m.id)(m); cdpWaiting.delete(m.id); }
  if (m.method === 'Runtime.exceptionThrown') consoleErrors.push(m.params.exceptionDetails?.exception?.description);
});
const cdp = (/** @type {string} */ method, params = {}) =>
  new Promise((r) => { const n = ++cdpId; cdpWaiting.set(n, r); page.send(JSON.stringify({ id: n, method, params })); });
const run = async (/** @type {string} */ expr) =>
  (/** @type {any} */ (await cdp('Runtime.evaluate', { expression: expr, awaitPromise: true, returnByValue: true }))).result?.result?.value;
await cdp('Runtime.enable');

// Espera que la interfície estigui connectada amb el motor (s'han creat els panells).
for (let i = 0; i < 90; i++) { if (await run(`!!document.querySelector('#stream .js-start')`)) break; await sleep(1000); }
await sleep(1500);
const closeDialogs = () => run(`document.querySelectorAll('dialog[open]').forEach((d) => d.close('cancel')); true`);
await closeDialogs();

// --- El motor (WebSocket, amb el testimoni de la carpeta de mostra) ---
const info = JSON.parse(fs.readFileSync(path.join(DATA, 'engine.json'), 'utf8'));
const engine = new WebSocket(`ws://127.0.0.1:${info.port}/ws?token=${info.token}`);
await new Promise((r) => engine.addEventListener('open', r));
let engineId = 1000;
const engineWaiting = new Map();
engine.addEventListener('message', (e) => {
  const m = JSON.parse(e.data);
  if (m.id && engineWaiting.has(m.id)) { engineWaiting.get(m.id)(m); engineWaiting.delete(m.id); }
});
/** Envia una ordre al motor (docs/protocol.md) i en retorna el resultat. */
async function eng(/** @type {string} */ cmd, params = {}) {
  const n = ++engineId;
  const reply = await new Promise((r) => { engineWaiting.set(n, r); engine.send(JSON.stringify({ v: 1, id: n, cmd, ...params })); });
  if (!reply.ok) throw new Error(`${cmd}: ${JSON.stringify(reply.error)}`);
  return reply.result;
}

finish = async () => {
  console.log(consoleErrors.length ? `Errors de la consola: ${JSON.stringify(consoleErrors)}` : 'Cap error a la consola.');
  try { await eng('engine.shutdown', { force: true }); } catch { /* ja aturat */ }
  await sleep(1000);
  electron.kill();
  process.exit(0);
};

if (phase === 'prepara') {
  const { settings } = await eng('settings.get');
  await eng('settings.set', { program: { name: 'Matins' }, layout: { players: 3, recorders: 2 }, ui: { zoom: 0.8, recordersPerRow: 2 } });
  await eng('profile.rename', { profile: settings.profile.id, name: 'Mostra' });
  console.log('Perfil «Mostra» preparat. Ara: node captures.mjs captura');
  await finish();
}

// --- captura: dispositius (els d'aquest ordinador; si no hi ha la Sound Blaster, el primer que hi hagi) ---
const { devices } = await eng('devices.list', { kind: 'all' });
const pick = (/** @type {string} */ kind, /** @type {string} */ prefix) => {
  const active = devices.filter((/** @type {any} */ d) => d.kind === kind && d.status === 'active');
  return (active.find((/** @type {any} */ d) => d.name.startsWith(prefix)) ?? active[0])?.id;
};
const output = pick('output', 'Altavoces - Sound Blaster');
const cueOutput = pick('output', 'Altavoces - Realtek');
const input = pick('input', 'Lo que se oye');
for (const p of ['1', '2', '3']) await eng('player.setDevice', { player: p, deviceId: output });
for (const r of ['1', '2']) await eng('recorder.setDevice', { recorder: r, deviceId: input });
await eng('cue.setDevice', { deviceId: cueOutput });
await eng('cart.setDevice', { deviceId: output });
await eng('stream.setDevice', { deviceId: input });

// Enregistradors, processament i llistes com a D-139.
await eng('recorder.configure', { recorder: '1', name: 'Programa' });
await eng('recorder.configure', { recorder: '2', name: 'Línia 2' });
await eng('recorder.setLimiter', { recorder: '1', enabled: true });
await eng('recorder.setLeveler', { recorder: '1', enabled: true });
await eng('player.setNormalization', { player: '1', enabled: true });
await eng('player.setProcessing', { player: '1', preset: 'sqrsoft-la-central' });
await eng('player.setLeveler', { player: '3', enabled: true });

const file = (/** @type {string} */ folder, /** @type {string} */ name) => path.join(AUDIO, folder, name + '.wav');
await eng('playlist.addSection', { player: '1', title: 'Primera hora', color: 'green' });
const first = (await eng('playlist.add', { player: '1', paths: [file('Musica', 'Artista A - Cançó del matí'), file('Musica', 'Artista B - Carretera')] })).ids;
await eng('playlist.addSection', { player: '1', title: 'Segona hora', color: 'blue' });
await eng('playlist.add', { player: '1', paths: [file('Musica', 'Artista C - Sol de tarda'), file('Musica', "Grup D - Nit d'estiu")] });
await eng('playlist.setFlags', { player: '1', itemId: first[0], autoNext: true });
const cuts = (await eng('playlist.add', { player: '2', paths: ["Sintonia d'entrada", 'Cortineta notícies', 'Falca agenda', 'Sintonia de sortida'].map((n) => file('Talls', n)) })).ids;
await eng('playlist.setFlags', { player: '2', itemId: cuts[1], loop: true });
await eng('playlist.addSection', { player: '3', title: 'Entrevista', color: 'orange' });
await eng('playlist.add', { player: '3', paths: [file('Programa', 'Entrevista')] });
await eng('playlist.addSection', { player: '3', title: 'Esports', color: 'red' });
await eng('playlist.add', { player: '3', paths: [file('Programa', 'Crònica esportiva'), file('Programa', 'Previsió del temps')] });

// Streaming (un servidor d'exemple: no s'hi connecta).
await eng('stream.saveServer', { server: { name: 'Ona Bages', type: 'shoutcast', host: 'radio.example.cat', port: 8000, password: 'mostra' } });
await eng('stream.setQuality', { bitrateKbps: 128, sampleRate: 44100, mono: false });
await eng('stream.setTitle', { title: 'Directe des de la plaça' });
await eng('stream.setLeveler', { enabled: true });
await eng('stream.setLimiter', { enabled: true });

// Finestra de 1200 × 900 (amb el zoom al 80 %, la pàgina en veu 1500 × 1125) i panell lateral plegat.
await run(`window.resizeTo(1200, 900); true`);
await sleep(15000); // la comprovació de les pistes (durades i canal buit: MONO) triga uns segons
await closeDialogs();

/**
 * Captura de la finestra o d'un tros. Les mides de la pàgina s'han de multiplicar pel zoom (devicePixelRatio).
 * @param {string} name
 * @param {string} [selector] només aquest element
 * @param {string} [untilSelector] fins a sota d'aquest altre element (p. ex. els botons d'un panell que és molt alt)
 */
async function shot(name, selector, untilSelector) {
  let clip;
  if (selector) {
    clip = await run(`(() => {
      const k = window.devicePixelRatio;
      const r = document.querySelector(${JSON.stringify(selector)}).getBoundingClientRect();
      const until = ${JSON.stringify(untilSelector ?? '')};
      const bottom = until ? document.querySelector(until).getBoundingClientRect().bottom + 14 : r.bottom;
      return { x: r.x * k, y: r.y * k, width: r.width * k, height: (bottom - r.y) * k, scale: 1 };
    })()`);
  }
  const r = /** @type {any} */ (await cdp('Page.captureScreenshot', { format: 'jpeg', quality: 88, ...(clip ? { clip } : {}) }));
  fs.writeFileSync(path.join(SHOTS, name + '.jpg'), Buffer.from(r.result.data, 'base64'));
  console.log('Captura:', name);
}

// Els noms són els de incrusta.mjs (cada un hi diu a quina imatge del manual va).
await shot('principal');
await shot('enregistrador', '.recorder');
await run(`document.querySelector('.side-btn[data-tab="stream"]').click(); true`);
await sleep(2500); // la finestra es fa més ampla
await shot('panell-lateral');
await shot('streaming', '#stream', '#stream .controls');

console.log(`Captures a ${SHOTS}. Mira-les i després: node incrusta.mjs`);
await finish();
