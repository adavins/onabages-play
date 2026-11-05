'use strict';

// Genera la icona de Windows (app/assets/icona.ico) a partir del logotip (app/assets/logo.svg).
// Es fa servir el mateix Electron de l'aplicació per dibuixar l'SVG a cada mida: no cal cap programa més.
//
// Ús (des de l'arrel del projecte), cada vegada que canviï el logotip:
//   app\node_modules\.bin\electron tools\icona\generar-icona.js
//
// El fitxer .ico porta una imatge PNG per a cada mida que fa servir Windows
// (barra de tasques, Explorador, escriptori, barra de títol...).

const { app, BrowserWindow } = require('electron');
const path = require('node:path');
const fs = require('node:fs');

const ROOT = path.resolve(__dirname, '..', '..');
const SOURCE = path.join(ROOT, 'app', 'assets', 'logo.svg');
const TARGET = path.join(ROOT, 'app', 'assets', 'icona.ico');
const PREVIEW = path.join(__dirname, 'icona-mostra.png'); // només per revisar-la; no va a l'aplicació
const SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

app.disableHardwareAcceleration();

app.whenReady().then(async () => {
  try {
    const svg = fs.readFileSync(SOURCE, 'utf8');
    const win = new BrowserWindow({ show: false, webPreferences: { offscreen: true } });
    await win.loadURL('data:text/html,<!DOCTYPE html><html><body></body></html>');

    // Dibuixa l'SVG a cada mida en un <canvas> i en retorna el PNG (base64).
    const images = await win.webContents.executeJavaScript(`
      (async () => {
        const url = 'data:image/svg+xml;base64,' + ${JSON.stringify(Buffer.from(svg).toString('base64'))};
        const img = new Image();
        img.src = url;
        await img.decode();
        const result = {};
        for (const size of ${JSON.stringify(SIZES)}) {
          const canvas = document.createElement('canvas');
          canvas.width = canvas.height = size;
          const ctx = canvas.getContext('2d');
          ctx.imageSmoothingQuality = 'high';
          ctx.drawImage(img, 0, 0, size, size);
          result[size] = canvas.toDataURL('image/png').split(',')[1];
        }
        // Mostra per revisar-les: totes les mides en una fila, sobre fons fosc i clar.
        const gap = 12, width = ${SIZES.reduce((a, b) => a + b, 0)} + gap * (${SIZES.length} + 1);
        const sheet = document.createElement('canvas');
        sheet.width = width; sheet.height = (256 + gap * 2) * 2;
        const s = sheet.getContext('2d');
        s.fillStyle = '#202124'; s.fillRect(0, 0, width, 256 + gap * 2);
        s.fillStyle = '#f3f3f3'; s.fillRect(0, 256 + gap * 2, width, 256 + gap * 2);
        for (const row of [0, 1]) {
          let x = gap;
          for (const size of ${JSON.stringify(SIZES)}) {
            const tile = new Image();
            tile.src = 'data:image/png;base64,' + result[size];
            await tile.decode();
            s.drawImage(tile, x, row * (256 + gap * 2) + gap + (256 - size));
            x += size + gap;
          }
        }
        result.preview = sheet.toDataURL('image/png').split(',')[1];
        return result;
      })()
    `);

    const pngs = SIZES.map((size) => ({ size, data: Buffer.from(images[size], 'base64') }));
    fs.writeFileSync(TARGET, buildIco(pngs));
    fs.writeFileSync(PREVIEW, Buffer.from(images.preview, 'base64'));
    console.log(`Fet: ${TARGET} (${SIZES.join(', ')} píxels)`);
    console.log(`Mostra per revisar-la: ${PREVIEW}`);
    app.exit(0);
  } catch (err) {
    console.error('ERROR:', err);
    app.exit(1);
  }
});

/**
 * Format .ico: una capçalera, una entrada per imatge i les imatges (PNG) una darrere l'altra.
 * @param {{ size: number, data: Buffer }[]} images
 */
function buildIco(images) {
  const header = Buffer.alloc(6);
  header.writeUInt16LE(0, 0); // reservat
  header.writeUInt16LE(1, 2); // 1 = icona
  header.writeUInt16LE(images.length, 4);

  const entries = [];
  let offset = 6 + 16 * images.length;
  for (const { size, data } of images) {
    const entry = Buffer.alloc(16);
    entry.writeUInt8(size >= 256 ? 0 : size, 0); // amplada (0 = 256)
    entry.writeUInt8(size >= 256 ? 0 : size, 1); // alçada
    entry.writeUInt8(0, 2); // colors de la paleta (cap)
    entry.writeUInt8(0, 3); // reservat
    entry.writeUInt16LE(1, 4); // plans
    entry.writeUInt16LE(32, 6); // bits per píxel
    entry.writeUInt32LE(data.length, 8);
    entry.writeUInt32LE(offset, 12);
    entries.push(entry);
    offset += data.length;
  }
  return Buffer.concat([header, ...entries, ...images.map((i) => i.data)]);
}
