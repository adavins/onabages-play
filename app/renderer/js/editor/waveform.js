// @ts-check
// Forma d'ona de l'editor (fase 3, pas 4): dibuix en un <canvas>, zoom, desplaçament,
// cursor, selecció i posició de reproducció. També la vista petita de tot el fitxer (a sota).
//
// Totes les posicions són en FOTOGRAMES DE LA VERSIÓ EDITADA (el que es veu). El motor dona
// els pics en fotogrames de l'ORIGINAL i la llista de trossos que es queden (segments):
// aquí es fa la correspondència.

/** Colors (els mateixos tons que la resta de la interfície; vegeu css/editor.css). */
const COLORS = {
  background: '#16191d',
  lane: '#1d2126',
  center: '#2c323a',
  wave: '#5aa9e6',
  missing: '#2a2f36',
  selection: 'rgba(255, 193, 7, 0.22)',
  selectionEdge: '#ffc107',
  cut: '#e35d6a',
  cursor: '#ffffff',
  playhead: '#3ddc84',
  ruler: '#9aa4ae',
  rulerLine: '#3a414a',
  overviewView: 'rgba(255, 255, 255, 0.18)',
  mark: '#3dd5f3',        // marques de l'enregistrament (pas 23c): el blau del botó Marca
  markText: '#000000',
};

const RULER_HEIGHT = 22;

/**
 * @typedef {{ start: number, end: number }} Range  fotogrames [start, end)
 * @typedef {{ number: number, frame: number, name?: string | null }} Mark  marca de l'enregistrament (fotograma editat)
 * @typedef {{
 *   onCursor?: (frame: number) => void,
 *   onSelection?: (range: Range | null) => void,
 *   onClick?: (frame: number) => void,
 *   onView?: () => void,
 *   onMarkMove?: (number: number, frame: number) => void,
 * }} WaveformCallbacks
 */

export class Waveform {
  // Dades
  /** @type {Uint8Array} */ #peaks = new Uint8Array(0);   // de l'original: 2 bytes (E, D) per fotograma
  #peaksDone = 0;                                            // fotogrames de l'original amb pic
  /** Guany dels fosos per fotograma de l'original (1 = sense canvi; null = cap fos). @type {Float32Array | null} */
  #gains = null;
  /** @type {Int32Array} */ #map = new Int32Array(0);       // editat → original
  /** @type {number[]} */ #cuts = [];                       // fotogrames (editats) on hi ha un tall
  /** Marques de l'enregistrament (pas 23c), per ordre. @type {Mark[]} */ #marks = [];
  frameSeconds = 0.026;

  // Vista
  #start = 0;          // primer fotograma visible
  #perPixel = 1;       // fotogrames per píxel (zoom)

  // Marques
  cursor = 0;
  /** @type {Range | null} */ selection = null;
  /** @type {number | null} */ playhead = null;   // fotograma on sona (null = no sona)

  #canvas;
  #overview;
  #callbacks;
  #drag = /** @type {{ anchor: number, moved: boolean } | null} */ (null);
  /** Etiquetes de les marques dibuixades (píxels del canvas), per agafar-les amb el ratolí (pas 23f). @type {{ number: number, x0: number, x1: number }[]} */
  #markBoxes = [];
  /** Marca que s'arrossega: on era (per tornar-hi amb Esc) i si s'ha mogut. @type {{ mark: Mark, from: number, moved: boolean } | null} */
  #markDrag = null;
  #overviewDrag = false;
  #pending = false;

  /**
   * @param {HTMLCanvasElement} canvas
   * @param {HTMLCanvasElement} overview
   * @param {WaveformCallbacks} callbacks
   */
  constructor(canvas, overview, callbacks) {
    this.#canvas = canvas;
    this.#overview = overview;
    this.#callbacks = callbacks;

    canvas.addEventListener('mousedown', (e) => this.#onMouseDown(e));
    // Damunt de l'etiqueta d'una marca, el cursor diu que es pot moure (pas 23f).
    canvas.addEventListener('mousemove', (e) => {
      if (!this.#drag && !this.#markDrag) canvas.style.cursor = this.#markAt(e.offsetX, e.offsetY) ? 'ew-resize' : '';
    });
    window.addEventListener('mousemove', (e) => this.#onMouseMove(e));
    window.addEventListener('mouseup', () => this.#onMouseUp());
    canvas.addEventListener('wheel', (e) => this.#onWheel(e), { passive: false });
    // Doble clic (pas 23c): selecciona el tros entre les dues marques del voltant.
    canvas.addEventListener('dblclick', (e) => {
      if (this.frames === 0 || this.#marks.length === 0) return;
      this.selectBetweenMarks(this.#xToFrame(e.offsetX));
    });
    overview.addEventListener('mousedown', (e) => { this.#overviewDrag = true; this.#onOverview(e); });
    window.addEventListener('mousemove', (e) => { if (this.#overviewDrag) this.#onOverview(e); });
    window.addEventListener('mouseup', () => { this.#overviewDrag = false; });
    new ResizeObserver(() => this.draw()).observe(canvas);
  }

  // -------------------------------------------------------------------------
  // Dades
  // -------------------------------------------------------------------------

  /** Fotogrames de la versió editada. */
  get frames() {
    return this.#map.length;
  }

  /** Fitxer nou: buida els pics i prepara la memòria per a tots els de l'original. */
  reset(/** @type {number} */ originalFrames, /** @type {number} */ frameSeconds) {
    this.#peaks = new Uint8Array(originalFrames * 2);
    this.#peaksDone = 0;
    this.#gains = null;
    this.frameSeconds = frameSeconds;
    this.cursor = 0;
    this.selection = null;
    this.playhead = null;
  }

  /** Afegeix pics que han arribat del motor (fotogrames de l'original a partir de `start`). */
  addPeaks(/** @type {number} */ start, /** @type {Uint8Array} */ data) {
    this.#peaks.set(data.subarray(0, Math.max(0, this.#peaks.length - start * 2)), start * 2);
    this.#peaksDone = Math.max(this.#peaksDone, start + data.length / 2);
    this.requestDraw();
  }

  /**
   * Fosos (fase 4, pas 6c): [[fotograma de l'original, passos del grànul 1, passos del grànul 2], ...].
   * Cada pas és de 1,5 dB; silenceSteps o menys = silenci. L'ona es dibuixa amb el pic multiplicat
   * pel guany més alt dels grànuls de cada fotograma.
   * @param {[number, number, number][]} gains
   * @param {number} silenceSteps
   * @param {number} granulesPerFrame
   */
  setGains(gains, silenceSteps, granulesPerFrame) {
    if (!gains || gains.length === 0) {
      this.#gains = null;
    } else {
      const factor = (/** @type {number} */ steps) => (steps <= silenceSteps ? 0 : Math.pow(10, (steps * 1.5) / 20));
      const result = new Float32Array(this.#peaks.length / 2).fill(1);
      for (const [frame, g0, g1] of gains) {
        if (frame < result.length) result[frame] = Math.max(factor(g0), granulesPerFrame > 1 ? factor(g1) : 0);
      }
      this.#gains = result;
    }
    this.requestDraw();
  }

  /** Trossos de l'original que es queden: [[primer, quants], ...]. */
  setSegments(/** @type {[number, number][]} */ segments) {
    const total = segments.reduce((sum, [, count]) => sum + count, 0);
    const map = new Int32Array(total);
    const cuts = [];
    let pos = 0;
    for (const [first, count] of segments) {
      if (pos > 0) cuts.push(pos);
      for (let i = 0; i < count; i++) map[pos + i] = first + i;
      pos += count;
    }
    this.#map = map;
    this.#cuts = cuts;
    this.cursor = Math.min(this.cursor, total);
    if (this.selection && this.selection.end > total) this.selection = null;
    this.#clampView();
    this.requestDraw();
  }

  // -------------------------------------------------------------------------
  // Marques de l'enregistrament (pas 23c)
  // -------------------------------------------------------------------------

  /** Marques que envia el motor, ja a la versió editada. @param {Mark[]} marks */
  setMarks(marks) {
    this.#marks = [...marks].sort((a, b) => a.frame - b.frame);
    this.requestDraw();
  }

  get marks() {
    return this.#marks;
  }

  /**
   * Tros entre les dues marques del voltant de `frame`: des de la marca d'abans (o l'inici) fins a la de després
   * (o el final). Una marca just al punt compta com la d'abans.
   * @returns {Range | null}
   */
  rangeBetweenMarks(/** @type {number} */ frame) {
    if (this.#marks.length === 0 || this.frames === 0) return null;
    let start = 0;
    let end = this.frames;
    for (const mark of this.#marks) {
      const f = Math.round(mark.frame);
      if (f <= frame) start = f;
      else { end = f; break; }
    }
    return end > start ? { start, end } : null;
  }

  /** Selecciona el tros entre marques del voltant de `frame` (i el mostra). */
  selectBetweenMarks(/** @type {number} */ frame) {
    const range = this.rangeBetweenMarks(frame);
    if (!range) return;
    this.#select(range.start, range.end);
    this.reveal(range.start);
  }

  /** Si s'està arrossegant una marca, la torna on era (Esc). Retorna si n'hi havia cap. */
  cancelMarkDrag() {
    if (!this.#markDrag) return false;
    this.#markDrag.mark.frame = this.#markDrag.from;
    this.#markDrag = null;
    this.#canvas.style.cursor = '';
    this.requestDraw();
    return true;
  }

  /** L'etiqueta de marca que hi ha en aquest punt del canvas, si n'hi ha. */
  #markAt(/** @type {number} */ x, /** @type {number} */ y) {
    if (y < RULER_HEIGHT || y > RULER_HEIGHT + 19) return null;
    const box = this.#markBoxes.find((b) => x >= b.x0 - 3 && x <= b.x1);
    return box ? this.#marks.find((m) => m.number === box.number) ?? null : null;
  }

  /** Fotograma de la marca anterior (`direction` −1) o següent (+1) a `frame`; null si no n'hi ha. */
  markFrom(/** @type {number} */ frame, /** @type {1 | -1} */ direction) {
    const frames = this.#marks.map((m) => Math.round(m.frame));
    if (direction > 0) return frames.find((f) => f > frame) ?? null;
    return frames.filter((f) => f < frame).pop() ?? null;
  }

  // -------------------------------------------------------------------------
  // Vista
  // -------------------------------------------------------------------------

  /** Fotogrames que caben a la pantalla amb el zoom actual. */
  get visibleFrames() {
    return this.#width() * this.#perPixel;
  }

  get viewStart() {
    return this.#start;
  }

  /** Tot el fitxer a la pantalla. */
  zoomAll() {
    this.#perPixel = Math.max(this.frames / Math.max(1, this.#width()), 1 / 64);
    this.#start = 0;
    this.#viewChanged();
  }

  /**
   * Apropa (factor < 1) o allunya (factor > 1) mantenint al mateix lloc el fotograma `center`
   * (per defecte, el cursor si es veu; si no, el centre de la pantalla).
   */
  zoom(/** @type {number} */ factor, /** @type {number | undefined} */ center = undefined) {
    const width = this.#width();
    const anchor = center ?? (this.#isVisible(this.cursor) ? this.cursor : this.#start + this.visibleFrames / 2);
    const x = (anchor - this.#start) / this.#perPixel;
    const max = Math.max(this.frames / Math.max(1, width), 1 / 64);
    this.#perPixel = Math.min(Math.max(this.#perPixel * factor, 1 / 64), max); // com a molt, 64 píxels per fotograma
    this.#start = anchor - x * this.#perPixel;
    this.#viewChanged();
  }

  /** Desplaça la vista (en fotogrames; negatiu = cap a l'esquerra). */
  scrollBy(/** @type {number} */ frames) {
    this.#start += frames;
    this.#viewChanged();
  }

  /** Si `frame` no es veu, mou la vista perquè es vegi (a prop de l'esquerra, per veure el que ve). */
  reveal(/** @type {number} */ frame) {
    if (this.#isVisible(frame)) return;
    this.#start = frame - this.visibleFrames * 0.1;
    this.#viewChanged();
  }

  // -------------------------------------------------------------------------
  // Dibuix
  // -------------------------------------------------------------------------

  /** Torna a dibuixar al proper quadre de pantalla (diverses peticions seguides, un sol dibuix). */
  requestDraw() {
    if (this.#pending) return;
    this.#pending = true;
    requestAnimationFrame(() => {
      this.#pending = false;
      this.draw();
    });
  }

  draw() {
    this.#drawMain();
    this.#drawOverview();
  }

  #drawMain() {
    const canvas = this.#canvas;
    const ctx = this.#fit(canvas);
    if (!ctx) return;
    const ratio = window.devicePixelRatio || 1;
    const width = canvas.width / ratio;
    const height = canvas.height / ratio;

    ctx.fillStyle = COLORS.background;
    ctx.fillRect(0, 0, width, height);
    if (this.frames === 0) return;

    this.#drawRuler(ctx, width);

    // Dos carrils: esquerre a dalt i dret a baix.
    const laneTop = RULER_HEIGHT + 2;
    const laneHeight = (height - laneTop - 4) / 2;
    for (let channel = 0; channel < 2; channel++) {
      const top = laneTop + channel * (laneHeight + 2);
      ctx.fillStyle = COLORS.lane;
      ctx.fillRect(0, top, width, laneHeight);
      this.#drawChannel(ctx, channel, top, laneHeight, width);
      ctx.fillStyle = COLORS.center;
      ctx.fillRect(0, Math.round(top + laneHeight / 2), width, 1);
    }

    // Selecció
    if (this.selection) {
      const x0 = this.#frameToX(this.selection.start);
      const x1 = this.#frameToX(this.selection.end);
      ctx.fillStyle = COLORS.selection;
      ctx.fillRect(x0, RULER_HEIGHT, Math.max(1, x1 - x0), height - RULER_HEIGHT);
      ctx.fillStyle = COLORS.selectionEdge;
      ctx.fillRect(Math.round(x0), RULER_HEIGHT, 1, height - RULER_HEIGHT);
      ctx.fillRect(Math.round(x1), RULER_HEIGHT, 1, height - RULER_HEIGHT);
    }

    // Talls (on s'ha eliminat alguna cosa)
    ctx.strokeStyle = COLORS.cut;
    ctx.setLineDash([4, 3]);
    for (const cut of this.#cuts) {
      if (!this.#isVisible(cut)) continue;
      const x = Math.round(this.#frameToX(cut)) + 0.5;
      ctx.beginPath();
      ctx.moveTo(x, RULER_HEIGHT);
      ctx.lineTo(x, height);
      ctx.stroke();
    }
    ctx.setLineDash([]);

    this.#drawMarks(ctx, height);

    // Cursor i posició de reproducció
    this.#drawLine(ctx, this.cursor, COLORS.cursor, height);
    if (this.playhead !== null) this.#drawLine(ctx, this.playhead, COLORS.playhead, height, 2);
  }

  /** Marques: línia vertical i una etiqueta amb el número (i el nom, si en té) a dalt. */
  #drawMarks(/** @type {CanvasRenderingContext2D} */ ctx, /** @type {number} */ height) {
    ctx.font = 'bold 11px system-ui, sans-serif';
    ctx.textBaseline = 'middle';
    this.#markBoxes = [];
    for (const mark of this.#marks) {
      if (!this.#isVisible(mark.frame) && Math.round(mark.frame) !== this.frames) continue;
      const x = Math.round(this.#frameToX(mark.frame));
      ctx.fillStyle = COLORS.mark;
      ctx.fillRect(x, RULER_HEIGHT, 1, height - RULER_HEIGHT);
      // Mentre s'arrossega, l'etiqueta diu on quedarà.
      const dragging = this.#markDrag?.mark === mark;
      const label = mark.name ? `${mark.number} ${mark.name}` : String(mark.number);
      const text = dragging ? `${label} · ${formatTime(mark.frame * this.frameSeconds, true)}` : label;
      const w = ctx.measureText(text).width + 8;
      this.#markBoxes.push({ number: mark.number, x0: x, x1: x + w });
      ctx.fillRect(x, RULER_HEIGHT + 2, w, 15);
      ctx.fillStyle = COLORS.markText;
      ctx.fillText(text, x + 4, RULER_HEIGHT + 10);
    }
  }

  /** Columna a columna: el pic més alt dels fotogrames que hi caben. */
  #drawChannel(/** @type {CanvasRenderingContext2D} */ ctx, /** @type {number} */ channel,
    /** @type {number} */ top, /** @type {number} */ laneHeight, /** @type {number} */ width) {
    const mid = top + laneHeight / 2;
    const scale = (laneHeight / 2 - 1) / 255;
    const peaks = this.#peaks;
    const map = this.#map;
    const total = map.length;

    for (let x = 0; x < width; x++) {
      const f0 = Math.floor(this.#start + x * this.#perPixel);
      const f1 = Math.max(f0 + 1, Math.floor(this.#start + (x + 1) * this.#perPixel));
      if (f1 <= 0 || f0 >= total) continue;

      let max = -1;
      let missing = false;
      for (let f = Math.max(0, f0); f < Math.min(total, f1); f++) {
        const original = map[f];
        if (original >= this.#peaksDone) { missing = true; continue; }
        const v = Math.min(255, peaks[original * 2 + channel] * (this.#gains ? this.#gains[original] : 1));
        if (v > max) max = v;
      }

      if (max < 0) {
        if (missing) {
          // Encara no s'ha calculat: una franja grisa.
          ctx.fillStyle = COLORS.missing;
          ctx.fillRect(x, top + laneHeight * 0.3, 1, laneHeight * 0.4);
        }
        continue;
      }
      const h = Math.max(1, max * scale);
      ctx.fillStyle = COLORS.wave;
      ctx.fillRect(x, mid - h, 1, h * 2);
    }
  }

  /** Regle de temps, amb marques a intervals rodons segons el zoom. */
  #drawRuler(/** @type {CanvasRenderingContext2D} */ ctx, /** @type {number} */ width) {
    const secondsPerPixel = this.#perPixel * this.frameSeconds;
    const steps = [0.1, 0.25, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
    const step = steps.find((s) => s / secondsPerPixel >= 90) ?? 3600;
    const startSec = this.#start * this.frameSeconds;
    const first = Math.ceil(startSec / step) * step;

    ctx.fillStyle = COLORS.ruler;
    ctx.font = '11px system-ui, sans-serif';
    ctx.textBaseline = 'top';
    for (let t = first; ; t += step) {
      const x = (t / this.frameSeconds - this.#start) / this.#perPixel;
      if (x > width) break;
      ctx.fillStyle = COLORS.rulerLine;
      ctx.fillRect(Math.round(x), RULER_HEIGHT - 6, 1, 6);
      ctx.fillStyle = COLORS.ruler;
      ctx.fillText(formatTime(t, step < 1), Math.round(x) + 3, 4);
    }
    ctx.fillStyle = COLORS.rulerLine;
    ctx.fillRect(0, RULER_HEIGHT - 1, width, 1);
  }

  #drawLine(/** @type {CanvasRenderingContext2D} */ ctx, /** @type {number} */ frame, /** @type {string} */ color,
    /** @type {number} */ height, lineWidth = 1) {
    if (!this.#isVisible(frame) && frame !== this.frames) return;
    ctx.fillStyle = color;
    ctx.fillRect(Math.round(this.#frameToX(frame)), 0, lineWidth, height);
  }

  /** Vista petita de tot el fitxer, amb un rectangle on hi ha la part que es veu a dalt. */
  #drawOverview() {
    const canvas = this.#overview;
    const ctx = this.#fit(canvas);
    if (!ctx) return;
    const ratio = window.devicePixelRatio || 1;
    const width = canvas.width / ratio;
    const height = canvas.height / ratio;
    ctx.fillStyle = COLORS.background;
    ctx.fillRect(0, 0, width, height);
    const total = this.frames;
    if (total === 0) return;

    const perPixel = total / width;
    const mid = height / 2;
    ctx.fillStyle = COLORS.wave;
    for (let x = 0; x < width; x++) {
      const f0 = Math.floor(x * perPixel);
      const f1 = Math.max(f0 + 1, Math.floor((x + 1) * perPixel));
      let max = 0;
      // Amb fitxers llargs n'hi ha prou amb una mostra de cada columna.
      const stepF = Math.max(1, Math.floor((f1 - f0) / 64));
      for (let f = f0; f < Math.min(total, f1); f += stepF) {
        const original = this.#map[f];
        if (original >= this.#peaksDone) continue;
        const v = Math.min(255, Math.max(this.#peaks[original * 2], this.#peaks[original * 2 + 1]) * (this.#gains ? this.#gains[original] : 1));
        if (v > max) max = v;
      }
      const h = Math.max(0.5, (max / 255) * (mid - 1));
      ctx.fillRect(x, mid - h, 1, h * 2);
    }

    // Part visible a dalt
    const x0 = (this.#start / total) * width;
    const w = Math.max(3, (this.visibleFrames / total) * width);
    ctx.fillStyle = COLORS.overviewView;
    ctx.fillRect(x0, 0, w, height);
    ctx.fillStyle = COLORS.mark;
    for (const mark of this.#marks) ctx.fillRect(Math.round((mark.frame / total) * width), 0, 2, height * 0.35);
    ctx.fillStyle = COLORS.cursor;
    ctx.fillRect(Math.round((this.cursor / total) * width), 0, 1, height);
    if (this.playhead !== null) {
      ctx.fillStyle = COLORS.playhead;
      ctx.fillRect(Math.round((this.playhead / total) * width), 0, 2, height);
    }
  }

  // -------------------------------------------------------------------------
  // Ratolí
  // -------------------------------------------------------------------------

  /** @param {MouseEvent} e */
  #onMouseDown(e) {
    if (e.button !== 0 || this.frames === 0) return;
    const frame = this.#xToFrame(e.offsetX);

    // Etiqueta d'una marca: s'arrossega la marca (pas 23f), no es fa cap selecció.
    const mark = this.#markAt(e.offsetX, e.offsetY);
    if (mark) {
      this.#markDrag = { mark, from: mark.frame, moved: false };
      return;
    }

    if (e.shiftKey) {
      // Maj + clic: estén la selecció (o en fa una des del cursor).
      const anchor = this.selection
        ? (Math.abs(frame - this.selection.start) < Math.abs(frame - this.selection.end) ? this.selection.end : this.selection.start)
        : this.cursor;
      this.#select(anchor, frame);
      this.#drag = { anchor, moved: true };
      return;
    }

    this.#drag = { anchor: frame, moved: false };
    this.cursor = frame;
    this.selection = null;
    this.#callbacks.onCursor?.(frame);
    this.#callbacks.onSelection?.(null);
    this.requestDraw();
  }

  /** @param {MouseEvent} e */
  #onMouseMove(e) {
    if (this.#markDrag) {
      const r = this.#canvas.getBoundingClientRect();
      const frame = this.#xToFrame(Math.min(Math.max(e.clientX - r.left, 0), r.width));
      if (!this.#markDrag.moved && Math.abs(frame - this.#markDrag.from) / this.#perPixel < 3) return;
      this.#markDrag.moved = true;
      this.#markDrag.mark.frame = frame;
      this.requestDraw();
      return;
    }
    if (!this.#drag) return;
    const rect = this.#canvas.getBoundingClientRect();
    const x = e.clientX - rect.left;
    // Si s'arrossega fora de la finestra, la vista es desplaça.
    if (x < 0) this.scrollBy(x * this.#perPixel * 0.2);
    if (x > rect.width) this.scrollBy((x - rect.width) * this.#perPixel * 0.2);
    const frame = this.#xToFrame(Math.min(Math.max(x, 0), rect.width));
    if (!this.#drag.moved && Math.abs(frame - this.#drag.anchor) / this.#perPixel < 3) return; // un clic, no un arrossegament
    this.#drag.moved = true;
    this.#select(this.#drag.anchor, frame);
  }

  #onMouseUp() {
    if (this.#markDrag) {
      const { mark, moved } = this.#markDrag;
      this.#markDrag = null;
      this.requestDraw();
      if (moved) this.#callbacks.onMarkMove?.(mark.number, mark.frame);
      return;
    }
    // Un clic sense arrossegar (p. ex. per saltar a un altre punt mentre sona).
    if (this.#drag && !this.#drag.moved) this.#callbacks.onClick?.(this.cursor);
    this.#drag = null;
  }

  /** @param {number} a @param {number} b */
  #select(a, b) {
    const start = Math.min(a, b);
    const end = Math.max(a, b);
    this.selection = end > start ? { start, end } : null;
    this.cursor = start;
    this.#callbacks.onCursor?.(start);
    this.#callbacks.onSelection?.(this.selection);
    this.requestDraw();
  }

  /** Roda: desplaça; amb Ctrl, zoom on és el ratolí. @param {WheelEvent} e */
  #onWheel(e) {
    if (this.frames === 0) return;
    e.preventDefault();
    if (e.ctrlKey) {
      this.zoom(e.deltaY > 0 ? 1.25 : 0.8, this.#xToFrameRaw(e.offsetX));
    } else {
      const delta = Math.abs(e.deltaX) > Math.abs(e.deltaY) ? e.deltaX : e.deltaY;
      this.scrollBy(Math.sign(delta) * this.visibleFrames * 0.1);
    }
  }

  /** Clic o arrossegament a la vista petita: centra la vista allà. @param {MouseEvent} e */
  #onOverview(e) {
    if (this.frames === 0) return;
    const rect = this.#overview.getBoundingClientRect();
    const frame = ((e.clientX - rect.left) / rect.width) * this.frames;
    this.#start = frame - this.visibleFrames / 2;
    this.#viewChanged();
  }

  // -------------------------------------------------------------------------

  #width() {
    return this.#canvas.clientWidth || 1;
  }

  #isVisible(/** @type {number} */ frame) {
    return frame >= this.#start && frame <= this.#start + this.visibleFrames;
  }

  #frameToX(/** @type {number} */ frame) {
    return (frame - this.#start) / this.#perPixel;
  }

  /** Fotograma (enter, dins del fitxer) que hi ha a la posició x. */
  #xToFrame(/** @type {number} */ x) {
    return Math.min(Math.max(Math.round(this.#xToFrameRaw(x)), 0), this.frames);
  }

  #xToFrameRaw(/** @type {number} */ x) {
    return this.#start + x * this.#perPixel;
  }

  #clampView() {
    const maxStart = Math.max(0, this.frames - this.visibleFrames);
    this.#start = Math.min(Math.max(this.#start, 0), maxStart);
    if (this.#perPixel * this.#width() > this.frames && this.frames > 0) {
      this.#perPixel = Math.max(this.frames / this.#width(), 1 / 64);
      this.#start = 0;
    }
  }

  #viewChanged() {
    this.#clampView();
    this.requestDraw();
    this.#callbacks.onView?.();
  }

  /** Ajusta la mida interna del canvas a la de pantalla (nítid amb qualsevol escala de Windows). */
  #fit(/** @type {HTMLCanvasElement} */ canvas) {
    const ratio = window.devicePixelRatio || 1;
    const width = Math.max(1, Math.round(canvas.clientWidth * ratio));
    const height = Math.max(1, Math.round(canvas.clientHeight * ratio));
    if (canvas.width !== width || canvas.height !== height) {
      canvas.width = width;
      canvas.height = height;
    }
    const ctx = canvas.getContext('2d');
    ctx?.setTransform(ratio, 0, 0, ratio, 0, 0);
    return ctx;
  }
}

/**
 * Temps amb centèsimes: «1:05.32» o «1:02:05.32». Sense centèsimes si `fine` és fals (regle).
 * @param {number} seconds
 * @param {boolean} [fine]
 */
export function formatTime(seconds, fine = true) {
  const s = Math.max(0, seconds);
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = Math.floor(s % 60);
  const cs = Math.floor((s * 100) % 100);
  const base = h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(sec).padStart(2, '0')}` : `${m}:${String(sec).padStart(2, '0')}`;
  return fine ? `${base}.${String(cs).padStart(2, '0')}` : base;
}
