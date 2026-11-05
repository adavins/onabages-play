// @ts-check
// Panell lateral de la dreta (fase 4, pas 2b; D-062).
//
// - Plegat: només una franja d'icones (Àudios, Llistes, Recents, Preescolta).
// - Obert: la pestanya triada a dalt i la preescolta sempre a baix.
// - En obrir-lo, la finestra es fa més ampla (ho fa Electron), perquè els reproductors i els
//   enregistradors no canviïn de mida. Si no pot créixer, el panell surt per sobre (.side-overlay).
// - S'obre sol quan la preescolta es posa a sonar.

/** @typedef {import('../store.js').Store} Store */

/** @type {any} API de preload.js */
const bridge = /** @type {any} */ (window).onabages;

const TABS = ['audios', 'lists', 'recents'];
const TAB_STORAGE_KEY = 'obp.sideTab';

/**
 * @param {Store} store
 * @returns {{ open: (tab?: string) => Promise<void>, close: () => Promise<void>, refreshSize: () => Promise<void> }}
 */
export function setupSidePanel(store) {
  const side = /** @type {HTMLElement} */ (document.getElementById('side'));
  const measure = /** @type {HTMLElement} */ (side.querySelector('.side-measure'));
  const toggle = /** @type {HTMLElement} */ (side.querySelector('.side-btn-toggle'));
  const cueButton = /** @type {HTMLElement} */ (side.querySelector('.side-btn-cue'));

  let isOpen = false;
  /** La pestanya que es veu (es recorda d'una vegada per l'altra). */
  let tab = localStorage.getItem(TAB_STORAGE_KEY) ?? 'recents';
  if (!TABS.includes(tab)) tab = 'recents';
  /** Les ordres d'obrir i tancar es fan una darrere l'altra (la finestra triga una mica a canviar de mida). */
  let queue = Promise.resolve();

  showTab(tab);
  // Si la pàgina s'ha recarregat amb el panell obert, la finestra torna a la mida sense panell.
  bridge?.setSidePanel(false);

  side.querySelector('.side-strip')?.addEventListener('click', (e) => {
    const button = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest('.side-btn'));
    if (!button) return;
    const wanted = button.dataset.tab;
    if (button === toggle || wanted === 'cue') {
      // Plegar o desplegar (la preescolta sempre hi és, a baix).
      isOpen ? close() : open();
    } else if (wanted) {
      // Una pestanya: si ja és la que es veu, es plega; si no, s'hi va (obrint el panell si cal).
      isOpen && wanted === tab ? close() : open(wanted);
    }
  });

  // Preescolta: la icona s'encén mentre sona, i el panell s'obre sol quan es posa a sonar.
  // Si s'ha obert sol (estava plegat), es torna a plegar quan la preescolta s'atura (p. ex. amb
  // el botó 🎧 de la pista), tret que mentrestant l'operador hagi fet servir el panell.
  let cuePlaying = false;
  let openedByCue = false;
  store.addEventListener('cue', () => {
    const playing = store.cue?.state === 'playing';
    cueButton.classList.toggle('cue-on', playing);
    cueButton.title = playing ? 'Preescolta: sona' : 'Preescolta';
    if (playing && !cuePlaying && !isOpen) {
      openedByCue = true;
      open();
    } else if (!playing && cuePlaying && openedByCue) {
      openedByCue = false;
      close();
    }
    cuePlaying = playing;
  });
  // Qualsevol clic dins del panell (o a les icones) vol dir que l'operador l'està fent servir: ja no es plega sol.
  side.addEventListener('pointerdown', () => { openedByCue = false; });

  /** @param {string} name */
  function showTab(name) {
    tab = name;
    localStorage.setItem(TAB_STORAGE_KEY, name);
    for (const el of side.querySelectorAll('.side-tab')) {
      el.classList.toggle('active', /** @type {HTMLElement} */ (el).dataset.tab === name);
    }
    for (const el of side.querySelectorAll('.side-btn[data-tab]')) {
      const tabName = /** @type {HTMLElement} */ (el).dataset.tab;
      el.classList.toggle('active', tabName === name);
    }
  }

  /** Obre el panell (opcionalment, en una pestanya). @param {string} [name] */
  function open(name) {
    if (name) showTab(name);
    queue = queue.then(async () => {
      if (isOpen) return;
      // Primer la finestra es fa més ampla i després apareix el panell: així els reproductors no s'encongeixen.
      const mode = bridge ? await bridge.setSidePanel(true, measure.offsetWidth) : 'overlay';
      side.classList.toggle('side-overlay', mode !== 'grow');
      side.classList.add('open');
      isOpen = true;
      updateToggle();
    });
    return queue;
  }

  function close() {
    queue = queue.then(async () => {
      if (!isOpen) return;
      side.classList.remove('open', 'side-overlay');
      isOpen = false;
      updateToggle();
      await bridge?.setSidePanel(false);
    });
    return queue;
  }

  /** El zoom ha canviat: l'amplada del panell en píxels de pantalla també. */
  function refreshSize() {
    queue = queue.then(async () => {
      if (!isOpen || !bridge) return;
      await bridge.setSidePanel(false);
      const mode = await bridge.setSidePanel(true, measure.offsetWidth);
      side.classList.toggle('side-overlay', mode !== 'grow');
    });
    return queue;
  }

  function updateToggle() {
    toggle.title = isOpen ? 'Plegar el panell' : 'Obrir el panell';
    /** @type {HTMLElement} */ (toggle.querySelector('i')).className =
      `bi ${isOpen ? 'bi-chevron-double-right' : 'bi-chevron-double-left'}`;
  }

  return { open, close, refreshSize };
}
