// @ts-check
// Panell lateral de la dreta (fase 4, pas 2b; D-062).
//
// - Plegat: només una franja d'icones (Àudios, Llistes, Botonera, Streaming, Recents i, a baix, plegar/desplegar).
// - La icona de la Botonera es posa del seu color mentre hi sona alguna cosa (fase 5, pas 28c).
// - La icona de l'Streaming s'encén en vermell mentre s'emet, i en groc (amb una altra icona) mentre es connecta o
//   es reconnecta, també amb el panell plegat (fase 5, pas 31d).
// - Obert: la pestanya triada a dalt i la preescolta sempre a baix.
// - En obrir-lo, la finestra es fa més ampla (ho fa Electron), perquè els reproductors i els
//   enregistradors no canviïn de mida. Si no pot créixer, el panell surt per sobre (.side-overlay).
// - S'obre sol quan la preescolta es posa a sonar.

/** @typedef {import('../store.js').Store} Store */

/** @type {any} API de preload.js */
const bridge = /** @type {any} */ (window).onabages;

const TABS = ['audios', 'lists', 'cart', 'stream', 'recents'];

/** Com es veu la icona de l'Streaming a cada estat: classe, icona i ajuda (color + icona + text). */
const STREAM_LOOKS = {
  streaming:    { cls: 'stream-on',   icon: 'bi-broadcast',                 title: 'Streaming: s\'està emetent' },
  connecting:   { cls: 'stream-warn', icon: 'bi-hourglass-split',           title: 'Streaming: connectant amb el servidor…' },
  reconnecting: { cls: 'stream-warn', icon: 'bi-arrow-repeat',              title: 'Streaming: s\'ha tallat, es torna a connectar…' },
  error:        { cls: 'stream-warn', icon: 'bi-exclamation-triangle-fill', title: 'Streaming: error, l\'emissió s\'ha aturat' },
};
const TAB_STORAGE_KEY = 'obp.sideTab';

/**
 * @param {Store} store
 * @returns {{ open: (tab?: string) => Promise<void>, close: () => Promise<void>, refreshSize: () => Promise<void> }}
 */
export function setupSidePanel(store) {
  const side = /** @type {HTMLElement} */ (document.getElementById('side'));
  const measure = /** @type {HTMLElement} */ (side.querySelector('.side-measure'));
  const toggle = /** @type {HTMLElement} */ (side.querySelector('.side-btn-toggle'));

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
    if (button === toggle) {
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
    if (playing && !cuePlaying && !isOpen) {
      openedByCue = true;
      open();
    } else if (!playing && cuePlaying && openedByCue) {
      openedByCue = false;
      close();
    }
    cuePlaying = playing;
    updateToggle();
  });
  // Botonera: la seva icona s'encén (color + icona d'altaveu) mentre hi sona alguna cosa.
  const cartButton = /** @type {HTMLElement | null} */ (side.querySelector('.side-btn[data-tab="cart"]'));
  const updateCart = () => {
    const on = (store.cart?.playing?.length ?? 0) > 0;
    cartButton?.classList.toggle('cart-on', on);
    const icon = cartButton?.querySelector('i');
    if (icon) icon.className = `bi ${on ? 'bi-volume-up-fill' : 'bi-grid-3x3-gap-fill'}`;
    if (cartButton) cartButton.title = on ? 'Botonera: hi sona alguna cosa' : 'Botonera (teclat numèric)';
  };
  store.addEventListener('cart', updateCart);
  store.addEventListener('reset', updateCart);
  // Streaming: la seva icona diu si s'emet, si es connecta o si ha fallat.
  const streamButton = /** @type {HTMLElement | null} */ (side.querySelector('.side-btn[data-tab="stream"]'));
  const updateStream = () => {
    const look = /** @type {Record<string, { cls: string, icon: string, title: string }>} */ (STREAM_LOOKS)[store.stream?.state ?? ''];
    streamButton?.classList.toggle('stream-on', look?.cls === 'stream-on');
    streamButton?.classList.toggle('stream-warn', look?.cls === 'stream-warn');
    const icon = streamButton?.querySelector('i');
    if (icon) icon.className = `bi ${look?.icon ?? 'bi-broadcast'}`;
    if (streamButton) streamButton.title = look?.title ?? 'Streaming';
  };
  store.addEventListener('stream', updateStream);
  store.addEventListener('reset', updateStream);

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

  /**
   * Botó de plegar/desplegar. Amb el panell plegat i la preescolta sonant, es posa taronja i
   * parpelleja: és l'únic avís que sona (la preescolta no es veu).
   */
  function updateToggle() {
    const cueHidden = cuePlaying && !isOpen;
    toggle.classList.toggle('cue-on', cueHidden);
    toggle.title = isOpen ? 'Plegar el panell' : cueHidden ? 'La preescolta sona: obrir el panell' : 'Obrir el panell';
    // Amb l'avís, la icona són uns auriculars (color i icona, no només color).
    /** @type {HTMLElement} */ (toggle.querySelector('i')).className =
      `bi ${isOpen ? 'bi-chevron-double-left' : cueHidden ? 'bi-headphones' : 'bi-chevron-double-right'}`;
  }

  return { open, close, refreshSize };
}
