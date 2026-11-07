// @ts-check
// Finestra de l'editor (fase 3, pas 4; especificació 9). Només mostra i envia ordres:
// el fitxer, les edicions, la forma d'ona i la reproducció són al motor (docs/protocol.md, 4.10).

import { EngineClient } from '../engine-client.js';
import { choose } from '../ui/dialogs.js';
import { escapeHtml, fileName } from '../ui/format.js';
import { Waveform, formatTime } from './waveform.js';

/** @type {any} API de preload.js */
const bridge = /** @type {any} */ (window).onabages;

const $ = (/** @type {string} */ id) => /** @type {HTMLElement} */ (document.getElementById(id));
const els = {
  file: $('ed-file'),
  modified: $('ed-modified'),
  saved: $('ed-saved'),
  engine: $('ed-engine'),
  open: $('ed-open'),
  save: /** @type {HTMLButtonElement} */ ($('ed-save')),
  saveAs: /** @type {HTMLButtonElement} */ ($('ed-save-as')),
  saveRec: /** @type {HTMLButtonElement} */ ($('ed-save-rec')),
  saveSel: /** @type {HTMLButtonElement} */ ($('ed-save-sel')),
  empty: $('ed-empty'),
  busy: $('ed-busy'),
  busyText: $('ed-busy-text'),
  home: /** @type {HTMLButtonElement} */ ($('ed-home')),
  play: /** @type {HTMLButtonElement} */ ($('ed-play')),
  playIcon: /** @type {HTMLElement} */ ($('ed-play').querySelector('.js-play-icon')),
  playSel: /** @type {HTMLButtonElement} */ ($('ed-play-sel')),
  cursor: $('ed-cursor'),
  selection: $('ed-selection'),
  duration: $('ed-duration'),
  trimStart: /** @type {HTMLButtonElement} */ ($('ed-trim-start')),
  trimEnd: /** @type {HTMLButtonElement} */ ($('ed-trim-end')),
  del: /** @type {HTMLButtonElement} */ ($('ed-delete')),
  keep: /** @type {HTMLButtonElement} */ ($('ed-keep')),
  fadeIn: /** @type {HTMLButtonElement} */ ($('ed-fade-in')),
  fadeOut: /** @type {HTMLButtonElement} */ ($('ed-fade-out')),
  undo: /** @type {HTMLButtonElement} */ ($('ed-undo')),
  redo: /** @type {HTMLButtonElement} */ ($('ed-redo')),
  zoomIn: /** @type {HTMLButtonElement} */ ($('ed-zoom-in')),
  zoomOut: /** @type {HTMLButtonElement} */ ($('ed-zoom-out')),
  zoomAll: /** @type {HTMLButtonElement} */ ($('ed-zoom-all')),
  marks: $('ed-marks'),
  markPrev: /** @type {HTMLButtonElement} */ ($('ed-mark-prev')),
  markSel: /** @type {HTMLButtonElement} */ ($('ed-mark-sel')),
  markNext: /** @type {HTMLButtonElement} */ ($('ed-mark-next')),
  device: /** @type {HTMLSelectElement} */ ($('ed-device')),
  message: $('ed-message'),
};

/** @type {EngineClient | null} */
let client = null;
/** Últim estat de l'editor que ha enviat el motor (protocol 4.10). @type {any} */
let editor = null;
/** Fitxer del qual tenim la forma d'ona. */
let loadedPath = '';
/** Fitxer desat que ja es mostra (després de desar, la forma d'ona es torna a carregar: ja porta els talls i els fosos). */
let loadedSavedPath = '';
/** Fotogrames de l'original amb pic que ja hem demanat. */
let peaksFetched = 0;
let fetchingPeaks = false;
/** Segments que té la forma d'ona (per no refer-la si no canvien). */
let segmentsKey = '';
/** Últims fosos dibuixats (per no recalcular-los si no canvien). */
let loadedGainsKey = '';
/** Últimes marques dibuixades (pas 23c). */
let marksKey = '';
/** Sortides de so (per al desplegable). @type {any[]} */
let outputs = [];
let cueDeviceName = '';
/** Fitxer que s'ha demanat obrir abans d'estar connectats al motor. @type {string | null} */
let pendingFile = null;
/** Enregistraments acabats (protocol 4.6): per saber si el fitxer obert és un enregistrament per desar (pas 14b). @type {any[]} */
let recordings = [];
/**
 * Desat d'un enregistrament en curs (pas 14b): quan l'editor acabi de desar, l'enregistrament queda desat.
 * `inPlace`: s'ha substituït l'original (el fitxer no canvia de lloc).
 * @type {{ recordingId: string, path: string, inPlace: boolean } | null}
 */
let savingRecording = null;

const wave = new Waveform(
  /** @type {HTMLCanvasElement} */ ($('ed-wave')),
  /** @type {HTMLCanvasElement} */ ($('ed-overview')),
  { onCursor: () => renderTimes(), onSelection: () => renderControls(), onClick: () => jumpIfPlaying() },
);

setupButtons();
setupKeyboard();
setupDrop();
init();

// ---------------------------------------------------------------------------
// Connexió amb el motor
// ---------------------------------------------------------------------------

async function init() {
  if (!bridge) {
    setEngine('error', 'Obre l\'editor des d\'Electron');
    return;
  }
  // Si la finestra és nova, el fitxer arriba abans d'estar connectats: s'obre en connectar-se.
  bridge.onEditorOpenFile((/** @type {string} */ path) => {
    if (client?.connected) openFile(path);
    else pendingFile = path;
  });
  bridge.onEditorCloseRequested(onCloseRequested);
  try {
    const info = await bridge.getEngineInfo();
    client = new EngineClient(info.port, info.token);
    client.addEventListener('status', onStatus);
    client.addEventListener('ev:editor.changed', (e) => apply(/** @type {any} */ (e).detail.editor));
    client.addEventListener('ev:editor.position', (e) => showPlayhead(/** @type {any} */ (e).detail.position));
    client.addEventListener('ev:devices.changed', () => loadDevices());
    client.addEventListener('ev:recordings.changed', (e) => {
      recordings = /** @type {any} */ (e).detail.recordings ?? [];
      renderControls();
    });
    client.addEventListener('ev:cue.state', (e) => { cueDeviceName = deviceName(/** @type {any} */ (e).detail.cue.deviceId); renderDevice(); });
    client.connect();
  } catch (err) {
    setEngine('error', /** @type {Error} */ (err).message);
  }
}

/** @param {Event} e */
async function onStatus(e) {
  const state = /** @type {any} */ (e).detail.state;
  if (state === 'connected') {
    setEngine('connected');
    try {
      await loadDevices();
      const { editor: current } = await request('editor.get');
      loadedPath = '';      // tornar a demanar la forma d'ona (pot haver canviat mentre no hi érem)
      segmentsKey = '';
      apply(current);
      if (pendingFile) {
        const path = pendingFile;
        pendingFile = null;
        await openFile(path);
      }
    } catch (err) {
      showMessage(/** @type {Error} */ (err).message, true);
    }
  } else {
    setEngine(state === 'connecting' ? 'connecting' : 'error', state === 'connecting' ? 'Connectant…' : 'Sense motor');
  }
  renderControls();
}

/**
 * @param {'connecting' | 'connected' | 'error'} state
 * @param {string} [text]
 */
function setEngine(state, text) {
  const look = {
    connecting: ['status-connecting', 'bi-hourglass-split', 'Connectant…'],
    connected: ['status-connected', 'bi-check-circle-fill', 'Motor connectat'],
    error: ['status-error', 'bi-exclamation-octagon-fill', 'Sense motor'],
  }[state];
  els.engine.className = `status ${look[0]}`;
  els.engine.innerHTML = `<i class="bi ${look[1]}"></i> <span class="label">${escapeHtml(text ?? look[2])}</span>`;
  document.body.classList.toggle('engine-offline', state !== 'connected');
}

/** @param {string} cmd @param {object} [params] */
function request(cmd, params = {}) {
  if (!client?.connected) return Promise.reject(new Error('El motor no està connectat.'));
  return client.request(cmd, params);
}

/** Envia una ordre i, si falla, ho diu a la barra de missatges. */
async function send(/** @type {string} */ cmd, /** @type {object} */ params = {}) {
  try {
    const result = await request(cmd, params);
    if (result.editor) apply(result.editor);
    return result;
  } catch (err) {
    showMessage(/** @type {Error} */ (err).message, true);
    return null;
  }
}

// ---------------------------------------------------------------------------
// Estat
// ---------------------------------------------------------------------------

/** Aplica l'estat de l'editor que envia el motor. */
function apply(/** @type {any} */ state) {
  const previous = editor;
  editor = state;

  // Fitxer nou, o el que s'acaba de desar (encara que tingui el mateix nom): forma d'ona des de zero.
  const ready = state.open && !state.busy && state.frames !== null;
  const justSaved = !!state.savedPath && state.savedPath !== loadedSavedPath;
  if (!state.savedPath) loadedSavedPath = ''; // s'ha tornat a modificar: el proper desat també es recarrega
  if (ready && (state.path !== loadedPath || justSaved)) {
    loadedPath = state.path;
    loadedSavedPath = state.savedPath ?? '';
    loadedGainsKey = '';
    peaksFetched = 0;
    segmentsKey = '';
    wave.reset(state.originalFrames, state.frameSeconds);
  }
  if (!state.open) {
    loadedPath = '';
    segmentsKey = '';
    marksKey = '';
    wave.reset(0, 0.026);
    wave.setSegments([]);
    wave.setMarks([]);
  }

  if (ready) {
    const key = JSON.stringify(state.segments);
    if (key !== segmentsKey) {
      const first = segmentsKey === '';
      segmentsKey = key;
      wave.setSegments(state.segments);
      if (first) wave.zoomAll();
    }
    // Fosos (fase 4, pas 6c): l'ona es dibuixa amb el volum que tindrà.
    const gainsKey = JSON.stringify(state.gains ?? []);
    if (gainsKey !== loadedGainsKey) {
      loadedGainsKey = gainsKey;
      wave.setGains(state.gains ?? [], state.silenceSteps, state.granulesPerFrame);
    }
    // Marques de l'enregistrament (pas 23c): el motor les dona ja a la versió editada (segons).
    const newMarksKey = JSON.stringify(state.marks ?? []);
    if (newMarksKey !== marksKey) {
      marksKey = newMarksKey;
      wave.setMarks((state.marks ?? []).map((/** @type {any} */ m) => ({ number: m.number, frame: m.seconds / state.frameSeconds, name: m.name })));
    }
    fetchPeaks();
  }

  if (state.playback.state !== 'playing') wave.playhead = null;

  // Errors de les feines en segon pla (obrir, desar, forma d'ona, reproducció).
  if (state.error && state.error !== previous?.error) showMessage(state.error, true);
  if (state.peaksError && state.peaksError !== previous?.peaksError) showMessage(state.peaksError, true);
  if (state.playback.error && state.playback.error !== previous?.playback?.error) showMessage(state.playback.error, true);
  if (state.savedPath && state.savedPath !== previous?.savedPath) {
    // Desat d'un enregistrament (pas 14b): és aquest desat (savingRecording només hi és mentre se n'espera un).
    // No es comparen les rutes: el motor la torna completa (p. ex. «albertdavins» en lloc de «ALBERT~1»).
    // Si s'ha desat a un altre lloc, l'enregistrament passa a ser el fitxer que diu el motor.
    if (savingRecording) {
      finishRecordingSave(savingRecording.inPlace ? savingRecording : { ...savingRecording, path: state.savedPath });
    } else {
      showMessage(`Desat: ${state.savedPath}`);
    }
  }
  // Selecció desada (pas 23d): l'edició no canvia, només es diu on és.
  if (state.savedSelectionPath && state.savedSelectionPath !== previous?.savedSelectionPath) {
    showMessage(`Selecció desada: ${state.savedSelectionPath}`);
  }
  // Si el desat ha fallat, l'enregistrament no es toca.
  if (savingRecording && !state.busy && state.error) savingRecording = null;

  render();
}

/** Demana al motor els pics que encara no tenim (de mica en mica, mentre es calculen). */
async function fetchPeaks() {
  if (fetchingPeaks || !editor?.open || editor.peaksDone <= peaksFetched) return;
  fetchingPeaks = true;
  const path = loadedPath;
  try {
    const { peaks } = await request('editor.peaks', { from: peaksFetched });
    if (path !== loadedPath) return; // mentrestant s'ha obert un altre fitxer
    const bytes = Uint8Array.from(atob(peaks.data), (c) => c.charCodeAt(0));
    wave.addPeaks(peaks.start, bytes);
    peaksFetched = peaks.start + peaks.count;
  } catch (err) {
    showMessage(/** @type {Error} */ (err).message, true);
  } finally {
    fetchingPeaks = false;
  }
  if (editor?.peaksDone > peaksFetched) fetchPeaks();
}

function showPlayhead(/** @type {number} */ position) {
  if (!editor?.frameSeconds) return;
  const frame = position / editor.frameSeconds;
  wave.playhead = frame;
  wave.reveal(frame);
  wave.requestDraw();
}

// ---------------------------------------------------------------------------
// Dibuix dels controls
// ---------------------------------------------------------------------------

function render() {
  const open = !!editor?.open;
  els.file.textContent = open ? fileName(editor.path) : 'Cap fitxer obert';
  els.file.title = open ? editor.path : '';
  document.title = open ? `${fileName(editor.path)} — Editor` : 'Editor — Ona Bages Play';
  els.modified.classList.toggle('d-none', !editor?.modified);
  els.saved.classList.toggle('d-none', !editor?.savedPath || editor.modified);
  els.empty.classList.toggle('d-none', open);

  const busy = editor?.busy;
  els.busy.classList.toggle('d-none', !busy);
  if (busy) {
    const percent = Math.round((editor.progress ?? 0) * 100);
    els.busyText.textContent = busy === 'opening' ? `Obrint ${fileName(editor.path)}…` : `Desant… ${percent} %`;
  }

  renderControls();
  renderTimes();
  renderDevice();
}

function renderControls() {
  const connected = !!client?.connected;
  const ready = connected && !!editor?.open && !editor.busy && editor.frames !== null;
  const hasSel = ready && !!wave.selection;
  const playing = editor?.playback?.state === 'playing';

  els.open.toggleAttribute('disabled', !connected || !!editor?.busy);
  els.save.disabled = !ready || !editor.modified;
  els.saveAs.disabled = !ready;
  els.saveSel.disabled = !hasSel;
  // «Desar l'enregistrament…» (pas 14b): només si el fitxer obert és un enregistrament que no s'ha desat.
  // Llavors és el botó principal, i «Desar com…» passa a ser secundari.
  const rec = currentRecording();
  els.saveRec.classList.toggle('d-none', !rec);
  els.saveRec.disabled = !ready || !!savingRecording;
  els.saveAs.classList.toggle('btn-primary', !rec);
  els.saveAs.classList.toggle('btn-outline-secondary', !!rec);
  els.home.disabled = !ready;
  els.play.disabled = !ready;
  els.playSel.disabled = !hasSel;
  els.trimStart.disabled = !ready || wave.cursor <= 0;
  els.trimEnd.disabled = !ready || wave.cursor >= wave.frames;
  els.del.disabled = !hasSel;
  els.keep.disabled = !hasSel;
  els.fadeIn.disabled = !hasSel;
  els.fadeOut.disabled = !hasSel;
  els.undo.disabled = !ready || !editor.canUndo;
  els.redo.disabled = !ready || !editor.canRedo;
  els.zoomIn.disabled = !ready;
  els.zoomOut.disabled = !ready;
  els.zoomAll.disabled = !ready;
  // Marques (pas 23c): el grup només surt si el fitxer en té (les dels trossos eliminats no compten).
  const hasMarks = ready && wave.marks.length > 0;
  els.marks.classList.toggle('d-none', !hasMarks);
  els.markPrev.disabled = !hasMarks;
  els.markSel.disabled = !hasMarks;
  els.markNext.disabled = !hasMarks;
  els.device.disabled = !connected;

  // Play ↔ Stop: només es canvia la classe (si es reconstruís el botó, es perdrien clics).
  els.play.classList.toggle('btn-success', !playing);
  els.play.classList.toggle('btn-danger', playing);
  els.playIcon.className = `bi js-play-icon ${playing ? 'bi-stop-fill' : 'bi-play-fill'}`;
  els.play.title = playing ? 'Aturar (Espai)' : 'Reproduir des del cursor (Espai)';
}

function renderTimes() {
  const fs = editor?.frameSeconds ?? 0;
  const ready = !!editor?.open && editor.frames !== null;
  els.cursor.textContent = ready ? formatTime(wave.cursor * fs) : '—';
  const sel = wave.selection;
  els.selection.textContent = ready && sel
    ? `${formatTime(sel.start * fs)} – ${formatTime(sel.end * fs)} (${formatTime((sel.end - sel.start) * fs)})`
    : '—';
  els.duration.textContent = ready
    ? (editor.modified
      ? `${formatTime(editor.duration)} (abans ${formatTime(editor.originalDuration)})`
      : formatTime(editor.duration))
    : '—';
  els.duration.title = ready
    ? `${(editor.sampleRate / 1000).toLocaleString('ca-ES')} kHz · ${editor.bitrates.join('/')} kbps · fotogrames de ${Math.round(fs * 1000)} ms`
    : '';
  renderControls();
}

// ---------------------------------------------------------------------------
// Sortida de so
// ---------------------------------------------------------------------------

async function loadDevices() {
  try {
    const { devices } = await request('devices.list', { kind: 'output' });
    outputs = devices; // només les sortides actives
    const { cue, recordings: finished } = await request('state.get');
    cueDeviceName = deviceName(cue.deviceId);
    recordings = finished ?? [];
  } catch {
    outputs = [];
  }
  renderDevice(true);
}

function deviceName(/** @type {string | null} */ id) {
  return outputs.find((d) => d.id === id)?.name ?? '';
}

let deviceKey = '';
function renderDevice(force = false) {
  const chosen = editor?.playback?.output?.id ?? '';
  const key = JSON.stringify([chosen, cueDeviceName, outputs.map((d) => d.id)]);
  if (key === deviceKey && !force) return;
  deviceKey = key;
  const cueLabel = cueDeviceName ? `Com la preescolta (${cueDeviceName})` : 'Com la preescolta';
  els.device.innerHTML = [
    `<option value=""${chosen ? '' : ' selected'}>${escapeHtml(cueLabel)}</option>`,
    ...outputs.map((d) => `<option value="${escapeHtml(d.id)}"${d.id === chosen ? ' selected' : ''}>${escapeHtml(d.name)}</option>`),
  ].join('');
}

// ---------------------------------------------------------------------------
// Accions
// ---------------------------------------------------------------------------

/** Obre un fitxer (si hi ha canvis sense desar, abans ho confirma). */
async function openFile(/** @type {string} */ path) {
  if (!path || !(await confirmDiscard('Obrir un altre fitxer'))) return;
  showMessage('');
  await send('editor.open', { path });
}

async function chooseAndOpen() {
  const path = await bridge.openMp3File();
  if (path) openFile(path);
}

/** Si hi ha canvis sense desar, pregunta si es poden perdre. */
async function confirmDiscard(/** @type {string} */ title) {
  if (!editor?.modified) return true;
  const choice = await choose({
    title,
    icon: 'bi-exclamation-triangle-fill',
    message: `Hi ha canvis sense desar a «${fileName(editor.path)}».\nSi continues, es perdran (el fitxer original no es toca).`,
    buttons: [
      { label: 'Cancel·lar', value: 'cancel' },
      { label: 'Continuar sense desar', value: 'discard', style: 'danger' },
    ],
  });
  return choice === 'discard';
}

/** Desar a sobre de l'original: sempre ho confirma (no es pot desfer). */
async function saveOver() {
  if (!editor?.modified) return;
  const choice = await choose({
    title: 'Desar a sobre de l\'original',
    icon: 'bi-exclamation-triangle-fill',
    message: `Se substituirà «${fileName(editor.path)}» per la versió editada.\nNo es podrà desfer. Si vols conservar l'original, fes servir «Desar com…».`,
    buttons: [
      { label: 'Cancel·lar', value: 'cancel' },
      { label: 'Desar com…', value: 'as', icon: 'bi-floppy2' },
      { label: 'Substituir l\'original', value: 'over', style: 'danger', icon: 'bi-floppy' },
    ],
  });
  if (choice === 'as') return saveAs();
  if (choice === 'over') await saveInPlace();
}

/**
 * Substitueix l'original per la versió editada. Si és un enregistrament no desat, després queda desat on és
 * (l'enregistrador deixa de tenir-lo pendent).
 */
async function saveInPlace() {
  const item = currentRecording();
  if (item) {
    savingRecording = { recordingId: item.id, path: item.path, inPlace: true };
    renderControls();
  }
  if (!(await send('editor.save', { overwrite: true }))) savingRecording = null;
  renderControls();
}

/** Desar com a fitxer nou (si ja existeix, el diàleg de Windows ja ha demanat si es substitueix). */
async function saveAs() {
  if (!editor?.open) return;
  const path = await bridge.saveMp3File(editor.path);
  if (!path) return;
  await send('editor.save', { path, overwrite: true });
}

/**
 * Desar només la selecció en un fitxer nou (pas 23d). Proposa «<nom> (1.00 a 2.30).mp3», amb els temps del tros,
 * perquè se'n puguin desar uns quants seguits sense que es trepitgin. El que s'edita no canvia.
 */
async function saveSelection() {
  const sel = wave.selection;
  if (!editor?.open || !sel) return;
  const fs = editor.frameSeconds;
  const base = fileName(editor.path).replace(/\.mp3$/i, '');
  const path = await bridge.saveMp3File(editor.path, {
    title: 'Desar la selecció…',
    name: `${base} (${fileTime(sel.start * fs)} a ${fileTime(sel.end * fs)}).mp3`,
  });
  if (!path) return;
  await send('editor.saveSelection', { start: sel.start * fs, end: sel.end * fs, path, overwrite: true });
}

/** «1.05» o «1.02.05» (h.mm.ss): un temps que es pot posar en un nom de fitxer (sense «:»). */
function fileTime(/** @type {number} */ seconds) {
  const s = Math.floor(seconds);
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60;
  const two = (/** @type {number} */ n) => String(n).padStart(2, '0');
  return h > 0 ? `${h}.${two(m)}.${two(r)}` : `${m}.${two(r)}`;
}

/** L'enregistrament no desat que té obert l'editor, si n'és un (pas 14b). */
function currentRecording() {
  if (!editor?.open) return null;
  return recordings.find((i) => samePath(i.path, editor.path) && !i.saved && !i.busy) ?? null;
}

/**
 * «Desar l'enregistrament…» (fase 5, pas 14b; D-102): desa la versió editada on triï l'operador (el mateix diàleg
 * que l'enregistrador: el nom actual i l'última carpeta) i l'enregistrament passa a ser aquest fitxer, ja desat.
 * Així no cal tornar-lo a desar a l'enregistrador. L'original sense editar va a la paperera si n'hi ha còpia a
 * l'historial d'enregistraments (ho decideix el motor); si no, es queda on és.
 */
async function saveRecordingEdited() {
  const item = currentRecording();
  if (!item || savingRecording) return;
  const chosen = await bridge.saveRecordingFile(item.path);
  if (!chosen) return;
  const path = /\.mp3$/i.test(chosen) ? chosen : `${chosen}.mp3`;
  if (samePath(path, item.path)) {
    // El mateix nom i la mateixa carpeta: es substitueix l'original (sense canvis, només queda desat).
    if (editor.modified) await saveInPlace();
    else await finishRecordingSave({ recordingId: item.id, path: item.path, inPlace: true });
    return;
  }
  savingRecording = { recordingId: item.id, path, inPlace: false };
  renderControls();
  // Quan l'editor acabi de desar (editor.changed amb savedPath), apply() crida finishRecordingSave.
  if (!(await send('editor.save', { path, overwrite: true }))) savingRecording = null;
  renderControls();
}

/** L'editor ha desat la versió editada: l'enregistrament passa a ser aquest fitxer, desat. */
async function finishRecordingSave(/** @type {{ recordingId: string, path: string, inPlace: boolean }} */ saving) {
  savingRecording = null;
  try {
    if (saving.inPlace) {
      // Substituït on era: només queda desat (no es mou res ni va res a la paperera).
      await request('recording.save', { recordingId: saving.recordingId, path: saving.path });
      showMessage(`Enregistrament desat: ${saving.path}.`);
      renderControls();
      return;
    }
    const { original } = await request('recording.saveEdited', { recordingId: saving.recordingId, path: saving.path });
    const what = original.recycled
      ? 'L\'original sense editar és a la paperera (n\'hi ha una còpia a l\'historial d\'enregistraments).'
      : (original.kept ?? '');
    showMessage(`Enregistrament desat: ${saving.path}. ${what}`);
  } catch (err) {
    showMessage(`La versió editada s'ha desat a ${saving.path}, però l'enregistrament no s'ha pogut marcar com a desat: ${/** @type {Error} */ (err).message}`, true);
  }
  renderControls();
}

/** Mateix fitxer (Windows no distingeix majúscules). */
function samePath(/** @type {string | null | undefined} */ a, /** @type {string | null | undefined} */ b) {
  return !!a && !!b && a.toLowerCase() === b.toLowerCase();
}

async function togglePlay() {
  if (!editor?.open || editor.busy) return;
  if (editor.playback.state === 'playing') {
    await send('editor.stop');
    return;
  }
  const from = wave.cursor >= wave.frames ? 0 : wave.cursor * editor.frameSeconds;
  await send('editor.play', { from });
}

/** Mentre sona, moure el cursor (clic, Inici/Fi, fletxes) fa continuar des del punt nou. */
async function jumpIfPlaying() {
  if (editor?.playback?.state !== 'playing' || wave.cursor >= wave.frames) return;
  await send('editor.play', { from: wave.cursor * editor.frameSeconds });
}

async function playSelection() {
  const sel = wave.selection;
  if (!sel || !editor?.open) return;
  await send('editor.play', { from: sel.start * editor.frameSeconds, to: sel.end * editor.frameSeconds });
}

/** Elimina [start, end) (en fotogrames) i deixa el cursor on era el tall. */
async function deleteRange(/** @type {number} */ start, /** @type {number} */ end) {
  if (!editor?.open || end <= start) return;
  const fs = editor.frameSeconds;
  const result = await send('editor.delete', { start: start * fs, end: end * fs });
  if (result?.changed) {
    wave.selection = null;
    wave.cursor = start;
    renderTimes();
    wave.requestDraw();
  }
}

/** Fos d'entrada o de sortida a la selecció (sense recodificar; D-073). La selecció es manté, per sentir-lo. */
async function fadeSelection(/** @type {'editor.fadeIn' | 'editor.fadeOut'} */ cmd) {
  const sel = wave.selection;
  if (!sel || !editor?.open) return;
  const fs = editor.frameSeconds;
  await send(cmd, { start: sel.start * fs, end: sel.end * fs });
}

async function keepSelection() {
  const sel = wave.selection;
  if (!sel || !editor?.open) return;
  const fs = editor.frameSeconds;
  const result = await send('editor.keep', { start: sel.start * fs, end: sel.end * fs });
  if (result?.changed) {
    wave.selection = null;
    wave.cursor = 0;
    wave.zoomAll();
    renderTimes();
  }
}

let jumpTimer = 0;
function moveCursor(/** @type {number} */ frame) {
  wave.cursor = Math.min(Math.max(Math.round(frame), 0), wave.frames);
  wave.selection = null;
  wave.reveal(wave.cursor);
  wave.requestDraw();
  renderTimes();
  // Si sona, salta al punt nou quan s'ha deixat de moure (amb la tecla premuda no es salta a cada pas).
  clearTimeout(jumpTimer);
  jumpTimer = window.setTimeout(jumpIfPlaying, 200);
}

/** Cursor a la marca anterior o següent (pas 23c). */
function goToMark(/** @type {1 | -1} */ direction) {
  const frame = wave.markFrom(wave.cursor, direction);
  if (frame !== null) moveCursor(frame);
}

/** Selecciona el tros entre les marques del voltant del cursor (pas 23c). */
function selectBetweenMarks() {
  wave.selectBetweenMarks(wave.cursor);
  renderTimes();
}

function selectAll() {
  if (!wave.frames) return;
  wave.selection = { start: 0, end: wave.frames };
  wave.cursor = 0;
  wave.requestDraw();
  renderTimes();
}

// ---------------------------------------------------------------------------
// Botons, teclat i arrossegar
// ---------------------------------------------------------------------------

function setupButtons() {
  els.open.addEventListener('click', chooseAndOpen);
  els.save.addEventListener('click', saveOver);
  els.saveAs.addEventListener('click', saveAs);
  els.saveRec.addEventListener('click', saveRecordingEdited);
  els.saveSel.addEventListener('click', saveSelection);
  els.home.addEventListener('click', () => moveCursor(0));
  els.play.addEventListener('click', togglePlay);
  els.playSel.addEventListener('click', playSelection);
  els.trimStart.addEventListener('click', () => deleteRange(0, wave.cursor));
  els.trimEnd.addEventListener('click', () => deleteRange(wave.cursor, wave.frames));
  els.del.addEventListener('click', () => wave.selection && deleteRange(wave.selection.start, wave.selection.end));
  els.keep.addEventListener('click', keepSelection);
  els.fadeIn.addEventListener('click', () => fadeSelection('editor.fadeIn'));
  els.fadeOut.addEventListener('click', () => fadeSelection('editor.fadeOut'));
  els.undo.addEventListener('click', () => send('editor.undo'));
  els.redo.addEventListener('click', () => send('editor.redo'));
  els.zoomIn.addEventListener('click', () => wave.zoom(0.5));
  els.zoomOut.addEventListener('click', () => wave.zoom(2));
  els.zoomAll.addEventListener('click', () => wave.zoomAll());
  els.markPrev.addEventListener('click', () => goToMark(-1));
  els.markNext.addEventListener('click', () => goToMark(1));
  els.markSel.addEventListener('click', selectBetweenMarks);
  els.device.addEventListener('change', async () => {
    deviceKey = '';
    await send('editor.setDevice', { deviceId: els.device.value || null });
    renderDevice(true);
  });
}

/** Dreceres (semblants a les del mp3DirectCut). No s'activen mentre s'escriu en un camp. */
function setupKeyboard() {
  window.addEventListener('keydown', (e) => {
    const target = /** @type {HTMLElement} */ (e.target);
    if (target.closest('input, select, textarea, dialog')) return;
    const ctrl = e.ctrlKey || e.metaKey;
    const fs = editor?.frameSeconds || 0.026;
    const second = 1 / fs;

    /** @type {(() => void) | null} */
    let action = null;
    if (e.key === ' ' && e.shiftKey) action = playSelection;
    else if (e.key === ' ') action = togglePlay;
    else if (e.key === 'Delete' || e.key === 'Backspace') action = () => wave.selection && deleteRange(wave.selection.start, wave.selection.end);
    else if (ctrl && e.key.toLowerCase() === 'z' && e.shiftKey) action = () => send('editor.redo');
    else if (ctrl && e.key.toLowerCase() === 'z') action = () => send('editor.undo');
    else if (ctrl && e.key.toLowerCase() === 'y') action = () => send('editor.redo');
    else if (ctrl && e.key.toLowerCase() === 's' && e.shiftKey) action = saveAs;
    else if (ctrl && e.key.toLowerCase() === 's') action = saveOver;
    else if (ctrl && e.key.toLowerCase() === 'o') action = chooseAndOpen;
    else if (ctrl && e.key.toLowerCase() === 'a') action = selectAll;
    // Fosos: E (entrada) i S (sortida), sense Ctrl (Ctrl+S és desar).
    else if (!ctrl && e.key.toLowerCase() === 'e') action = () => fadeSelection('editor.fadeIn');
    else if (!ctrl && e.key.toLowerCase() === 's') action = () => fadeSelection('editor.fadeOut');
    // Marques (pas 23c): Re Pàg / Av Pàg hi salten; M selecciona el tros entre les del voltant.
    else if (e.key === 'PageUp') action = () => goToMark(-1);
    else if (e.key === 'PageDown') action = () => goToMark(1);
    else if (!ctrl && e.key.toLowerCase() === 'm') action = selectBetweenMarks;
    else if (e.key === 'Home') action = () => moveCursor(0);
    else if (e.key === 'End') action = () => moveCursor(wave.frames);
    else if (e.key === 'ArrowLeft') action = () => moveCursor(wave.cursor - second * (e.shiftKey ? 10 : 1));
    else if (e.key === 'ArrowRight') action = () => moveCursor(wave.cursor + second * (e.shiftKey ? 10 : 1));
    else if (e.key === '+' || (e.key === '=' && !e.shiftKey)) action = () => wave.zoom(0.5);
    else if (e.key === '-') action = () => wave.zoom(2);
    else if (e.key === '0') action = () => wave.zoomAll();
    else if (e.key === 'Escape') action = () => { wave.selection = null; wave.requestDraw(); renderTimes(); };

    if (action && !(editor?.busy)) {
      e.preventDefault();
      action();
    }
  });
}

/** Un MP3 arrossegat a la finestra s'obre. */
function setupDrop() {
  window.addEventListener('dragover', (e) => e.preventDefault());
  window.addEventListener('drop', (e) => {
    e.preventDefault();
    const file = e.dataTransfer?.files[0];
    if (!file) return;
    const path = bridge?.pathForFile(file);
    if (path && /\.mp3$/i.test(path)) openFile(path);
    else showMessage('L\'editor només obre fitxers MP3.', true);
  });
}

/** En tancar la finestra: si hi ha canvis sense desar, es confirma; el fitxer es tanca al motor. */
async function onCloseRequested() {
  if (!(await confirmDiscard('Tancar l\'editor'))) return;
  try {
    if (client?.connected) await client.request('editor.close', {}, 1500);
  } catch {
    // Es tanca igualment: l'original no s'ha tocat.
  }
  client?.close();
  bridge.confirmEditorClose();
}

let messageTimer = 0;
/** Missatge breu a la part de baix (els errors es queden més estona). */
function showMessage(/** @type {string} */ text, isError = false) {
  els.message.textContent = text;
  els.message.classList.toggle('ed-message-error', isError);
  els.message.classList.toggle('ed-message-visible', !!text);
  clearTimeout(messageTimer);
  if (text) messageTimer = window.setTimeout(() => els.message.classList.remove('ed-message-visible'), isError ? 10000 : 4000);
}
