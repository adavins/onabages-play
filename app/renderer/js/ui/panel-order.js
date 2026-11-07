// @ts-check
// Ordre dels reproductors i dels enregistradors a la pantalla (fase 5, pas 16; D-105).
//
// Cada panell té una nansa ⋮⋮ a la capçalera: s'arrossega i es deixa anar a sobre d'un altre panell
// del mateix tipus, i s'intercanvien. Els reproductors només amb reproductors; els enregistradors,
// només amb enregistradors (a qualsevol fila de la seva graella).
//
// L'ordre es desa al perfil (ui.playerOrder / ui.recorderOrder). A la pantalla s'aplica amb la propietat
// CSS «order»: els panells no es treuen mai del document, i així no es perd res (posició de la llista,
// mesuradors...). Només canvia on es veuen: el so i l'enregistrament no es toquen.

/** Tipus d'arrossegament: porta el tipus de panell al nom perquè es pugui llegir durant «dragover». */
const DRAG_TYPE = (/** @type {string} */ kind) => `application/x-obp-panel-${kind}`;

/**
 * Identificadors en l'ordre de la pantalla: primer els de l'ordre desat (els que existeixen),
 * i després els que no hi surten, per número.
 * @param {Iterable<string>} ids  els que hi ha ara
 * @param {string[] | undefined} order  l'ordre desat (pot tenir identificadors que ja no hi són)
 * @returns {string[]}
 */
export function orderedIds(ids, order) {
  const all = [...ids].sort((a, b) => Number(a) - Number(b));
  const first = (order ?? []).filter((id) => all.includes(id));
  return [...first, ...all.filter((id) => !first.includes(id))];
}

/**
 * Posa cada panell al seu lloc (propietat CSS «order»).
 * @param {Map<string, { element: HTMLElement }>} panels
 * @param {string[]} order  identificadors en l'ordre de la pantalla
 */
export function applyOrder(panels, order) {
  order.forEach((id, index) => {
    const panel = panels.get(id);
    if (panel) panel.element.style.order = String(index);
  });
}

/**
 * Activa l'arrossegament dels panells d'un contenidor (#players o #recorders).
 * @param {HTMLElement} container
 * @param {'player' | 'recorder'} kind
 * @param {(fromId: string, toId: string) => void} onSwap  s'han deixat anar: intercanviar-los
 */
export function setupPanelDrag(container, kind, onSwap) {
  const type = DRAG_TYPE(kind);
  /** @type {HTMLElement | null} */
  let target = null;

  /** El panell (del tipus d'aquest contenidor) que hi ha sota l'esdeveniment. */
  const panelAt = (/** @type {Event} */ e) => {
    const el = /** @type {HTMLElement | null} */ (/** @type {HTMLElement} */ (e.target).closest?.('[data-panel-id]'));
    return el && el.parentElement === container ? el : null;
  };
  const setTarget = (/** @type {HTMLElement | null} */ el) => {
    if (target === el) return;
    target?.classList.remove('swap-target');
    target = el;
    target?.classList.add('swap-target');
  };

  container.addEventListener('dragstart', (e) => {
    const handle = /** @type {HTMLElement} */ (e.target).closest?.('.panel-handle');
    const panel = panelAt(e);
    if (!handle || !panel || !e.dataTransfer) return;
    e.dataTransfer.setData(type, panel.dataset.panelId ?? '');
    e.dataTransfer.effectAllowed = 'move';
    // La imatge que segueix el ratolí és tot el panell, agafat per la nansa.
    const box = panel.getBoundingClientRect();
    const grip = handle.getBoundingClientRect();
    e.dataTransfer.setDragImage(panel, grip.left - box.left + grip.width / 2, grip.top - box.top + grip.height / 2);
    // Un instant després (si no, la imatge també sortiria transparent).
    requestAnimationFrame(() => panel.classList.add('panel-dragging'));
  });

  // Fase de captura: s'ha de decidir abans que les llistes (que aturen l'esdeveniment) i que el panell.
  container.addEventListener('dragover', (e) => {
    if (!e.dataTransfer?.types.includes(type)) return;
    e.preventDefault();
    e.stopPropagation();
    const panel = panelAt(e);
    const ok = !!panel && !panel.classList.contains('panel-dragging');
    e.dataTransfer.dropEffect = ok ? 'move' : 'none';
    setTarget(ok ? panel : null);
  }, true);

  container.addEventListener('dragleave', (e) => {
    if (!container.contains(/** @type {Node | null} */ (e.relatedTarget))) setTarget(null);
  });

  container.addEventListener('drop', (e) => {
    if (!e.dataTransfer?.types.includes(type)) return;
    e.preventDefault();
    e.stopPropagation();
    const fromId = e.dataTransfer.getData(type);
    const toId = target?.dataset.panelId;
    setTarget(null);
    if (fromId && toId && fromId !== toId) onSwap(fromId, toId);
  }, true);

  container.addEventListener('dragend', () => {
    setTarget(null);
    container.querySelector('.panel-dragging')?.classList.remove('panel-dragging');
  });
}

/** Marca HTML de la nansa (va al principi de la capçalera de cada panell). */
export const HANDLE_HTML =
  '<span class="panel-handle" draggable="true" title="Arrossega per canviar-lo de lloc"><i class="bi bi-grip-vertical"></i></span>';
