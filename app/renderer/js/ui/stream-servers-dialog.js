// @ts-check
// Servidors d'streaming del perfil (fase 5, pas 31d; D-140, D-143): la llista, afegir-ne, canviar-los i esborrar-los.
// Les dades són les mateixes que al BUTT. El motor no envia mai les contrasenyes: si un servidor ja en té, el camp
// es deixa buit i, si no s'hi escriu res, es conserva la que hi havia.

import { escapeHtml } from './format.js';
import { choose } from './dialogs.js';

/** @typedef {import('../store.js').Store} Store */
/** @typedef {(cmd: string, params?: object) => Promise<any>} Request */

const TYPE_NAMES = { shoutcast: 'Shoutcast v2', icecast: 'Icecast' };

/**
 * Finestra amb la llista de servidors. Cada canvi es desa de seguida al perfil.
 * @param {Store} store
 * @param {Request} request
 */
export function manageStreamServers(store, request) {
  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog stream-servers-dialog';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title"><i class="bi bi-hdd-network"></i> Servidors d'streaming</h2>
      <p class="form-text">Els servidors són del perfil. Les dades són les mateixes que al BUTT.</p>
      <div class="stream-server-list js-list"></div>
      <div class="error-text js-error"></div>
      <div class="obp-dialog-buttons">
        <button class="btn btn-outline-light js-add" type="button"><i class="bi bi-plus-lg"></i> Afegir un servidor…</button>
        <button class="btn btn-primary" value="close">Tancar</button>
      </div>
    </form>`;
  document.body.appendChild(dialog);

  const list = /** @type {HTMLElement} */ (dialog.querySelector('.js-list'));
  const error = /** @type {HTMLElement} */ (dialog.querySelector('.js-error'));
  const active = () => ['connecting', 'streaming', 'reconnecting'].includes(store.stream?.state);

  function render() {
    const s = store.stream;
    const servers = /** @type {any[]} */ (s?.servers ?? []);
    if (servers.length === 0) {
      list.innerHTML = '<div class="placeholder-text">Encara no hi ha cap servidor. Afegeix-ne un amb les dades del BUTT.</div>';
      return;
    }
    list.innerHTML = servers.map((server) => {
      const selected = server.id === s.serverId;
      const inUse = selected && active();
      return `
        <div class="stream-server${selected ? ' selected' : ''}" data-id="${escapeHtml(server.id)}">
          <div class="stream-server-text">
            <div class="stream-server-name">${selected ? '<i class="bi bi-check-circle-fill" title="El triat"></i> ' : ''}${escapeHtml(server.name)}</div>
            <div class="stream-server-detail">${escapeHtml(describe(server))}</div>
          </div>
          <button class="btn btn-sm btn-outline-secondary js-edit" type="button" title="${inUse ? 'Atura l\'emissió per canviar-lo' : 'Canviar les dades'}"${inUse ? ' disabled' : ''}>
            <i class="bi bi-pencil"></i></button>
          <button class="btn btn-sm btn-outline-danger js-delete" type="button" title="${inUse ? 'S\'hi està emetent' : 'Esborrar-lo'}"${inUse ? ' disabled' : ''}>
            <i class="bi bi-trash"></i></button>
        </div>`;
    }).join('');
  }

  /** @param {any} [server] */
  async function edit(server) {
    const fields = await editStreamServer(server);
    if (!fields) return;
    try {
      const result = await request('stream.saveServer', { server: fields });
      store.stream = result.stream;
      error.textContent = '';
      render();
    } catch (err) {
      error.textContent = /** @type {Error} */ (err).message;
    }
  }

  /** @param {any} server */
  async function remove(server) {
    const choice = await choose({
      title: 'Esborrar el servidor',
      icon: 'bi-trash',
      message: `Vols esborrar el servidor «${server.name}» d'aquest perfil?`,
      buttons: [
        { label: 'Cancel·lar', value: 'cancel' },
        { label: 'Esborrar', value: 'delete', style: 'danger' },
      ],
    });
    if (choice !== 'delete') return;
    try {
      const result = await request('stream.deleteServer', { serverId: server.id });
      store.stream = result.stream;
      error.textContent = '';
      render();
    } catch (err) {
      error.textContent = /** @type {Error} */ (err).message;
    }
  }

  /** @type {HTMLElement} */ (dialog.querySelector('.js-add')).addEventListener('click', () => edit());
  list.addEventListener('click', (e) => {
    const target = /** @type {HTMLElement} */ (e.target);
    const row = /** @type {HTMLElement | null} */ (target.closest('.stream-server'));
    const server = store.stream?.servers?.find((/** @type {any} */ x) => x.id === row?.dataset.id);
    if (!server) return;
    if (target.closest('.js-edit')) edit(server);
    else if (target.closest('.js-delete')) remove(server);
  });

  // Si canvia l'emissor mentre és oberta (p. ex. comença a emetre), la llista es torna a pintar.
  const onStream = () => render();
  store.addEventListener('stream', onStream);
  render();
  dialog.showModal();
  dialog.addEventListener('close', () => {
    store.removeEventListener('stream', onStream);
    dialog.remove();
  });
}

/** «Shoutcast v2 · onabagescast.radioca.st:8000 · sid 1», per a la llista. @param {any} server */
export function describe(server) {
  const type = /** @type {Record<string, string>} */ (TYPE_NAMES)[server.type] ?? server.type;
  const where = `${server.host}:${server.port}`;
  const extra = server.type === 'icecast' ? server.mount : `sid ${server.streamId}`;
  return [type, where, extra, server.hasPassword ? null : 'sense contrasenya'].filter(Boolean).join(' · ');
}

/**
 * Formulari d'un servidor (nou, si no se'n passa cap). Retorna els camps per a stream.saveServer, o null.
 * @param {any} [server]
 * @returns {Promise<object | null>}
 */
function editStreamServer(server) {
  const isNew = !server;
  const s = server ?? { name: '', type: 'shoutcast', host: '', port: 8000, mount: '/stream', user: 'source', streamId: 1, hasPassword: false };
  const dialog = document.createElement('dialog');
  dialog.className = 'obp-dialog obp-form';
  dialog.innerHTML = `
    <form method="dialog">
      <h2 class="obp-dialog-title"><i class="bi bi-hdd-network"></i> ${isNew ? 'Servidor nou' : escapeHtml(s.name)}</h2>

      <label class="form-label" for="ss-name">Nom</label>
      <input id="ss-name" class="form-control mb-1" type="text" maxlength="40" value="${escapeHtml(s.name)}" placeholder="p. ex. Ona Bages">
      <div class="form-text mb-3">El veuen els oients com a nom de l'emissora.</div>

      <label class="form-label" for="ss-type">Tipus de servidor</label>
      <select id="ss-type" class="form-select mb-3">
        <option value="shoutcast"${s.type === 'shoutcast' ? ' selected' : ''}>Shoutcast v2</option>
        <option value="icecast"${s.type === 'icecast' ? ' selected' : ''}>Icecast</option>
      </select>

      <div class="stream-server-row">
        <div class="stream-server-host">
          <label class="form-label" for="ss-host">Adreça</label>
          <input id="ss-host" class="form-control" type="text" value="${escapeHtml(s.host)}" placeholder="p. ex. onabagescast.radioca.st" required>
        </div>
        <div class="stream-server-port">
          <label class="form-label" for="ss-port">Port</label>
          <input id="ss-port" class="form-control" type="number" min="1" max="65535" value="${escapeHtml(s.port)}" required>
        </div>
      </div>
      <div class="form-text mb-3 js-port-help"></div>

      <label class="form-label" for="ss-password">Contrasenya</label>
      <input id="ss-password" class="form-control mb-1" type="password" autocomplete="off"
             placeholder="${s.hasPassword ? 'La que ja hi ha (escriu-ne una per canviar-la)' : ''}">
      <div class="form-text mb-3">Es desa al perfil sense xifrar, com al BUTT.</div>

      <div class="js-shoutcast">
        <label class="form-label" for="ss-sid">Identificador del stream (SID)</label>
        <input id="ss-sid" class="form-control mb-1" type="number" min="1" max="999" value="${escapeHtml(s.streamId)}">
        <div class="form-text mb-3">Gairebé sempre 1.</div>
      </div>
      <div class="js-icecast">
        <label class="form-label" for="ss-mount">Punt de muntatge</label>
        <input id="ss-mount" class="form-control mb-3" type="text" value="${escapeHtml(s.mount)}" placeholder="/stream">
        <label class="form-label" for="ss-user">Usuari</label>
        <input id="ss-user" class="form-control mb-3" type="text" value="${escapeHtml(s.user)}" placeholder="source">
      </div>

      <div class="obp-dialog-buttons">
        <button class="btn btn-secondary js-cancel" type="button">Cancel·lar</button>
        <button class="btn btn-primary" value="save"><i class="bi bi-check-lg"></i> Desar</button>
      </div>
    </form>`;

  const $ = (/** @type {string} */ selector) => /** @type {HTMLInputElement} */ (dialog.querySelector(selector));
  const type = /** @type {HTMLSelectElement} */ (dialog.querySelector('#ss-type'));
  const showType = () => {
    const icecast = type.value === 'icecast';
    $('.js-icecast').classList.toggle('d-none', !icecast);
    $('.js-shoutcast').classList.toggle('d-none', icecast);
    $('.js-port-help').textContent = icecast
      ? 'El port del servidor Icecast (el mateix que al BUTT).'
      : 'El port del servidor, el mateix que al BUTT. Com el BUTT, l\'emissió es connecta al port següent (port + 1).';
  };
  type.addEventListener('change', showType);
  showType();
  /** @type {HTMLElement} */ (dialog.querySelector('.js-cancel')).addEventListener('click', () => dialog.close('cancel'));

  document.body.appendChild(dialog);
  dialog.showModal();
  $('#ss-name').focus();

  return new Promise((resolve) => {
    dialog.addEventListener('close', () => {
      if (dialog.returnValue !== 'save') {
        resolve(null);
      } else {
        /** @type {Record<string, any>} */
        const fields = {
          name: $('#ss-name').value.trim(),
          type: type.value,
          host: $('#ss-host').value.trim(),
          port: Number($('#ss-port').value),
          streamId: Number($('#ss-sid').value) || 1,
          mount: $('#ss-mount').value.trim(),
          user: $('#ss-user').value.trim(),
        };
        if (!isNew) fields.id = s.id;
        // Buit = es conserva la que hi havia (si n'hi havia cap).
        const password = $('#ss-password').value;
        if (password || isNew) fields.password = password;
        resolve(fields);
      }
      dialog.remove();
    });
  });
}
