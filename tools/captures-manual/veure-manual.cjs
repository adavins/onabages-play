// Pas 5 (opcional): obre el manual en una finestra amagada, va a una secció i en fa una captura, per comprovar-lo
// sense obrir el navegador. És el «main» d'Electron (per això és CommonJS):
//   ..\..\app\node_modules\electron\dist\electron.exe veure-manual.cjs streaming
// La captura queda a la carpeta de captures com a manual-<secció>.png.
const { app, BrowserWindow } = require('electron');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const anchor = process.argv[process.argv.length - 1];
const out = path.join(os.tmpdir(), 'OnaBagesCaptures', 'captures', `manual-${anchor}.png`);

app.whenReady().then(async () => {
  const win = new BrowserWindow({ width: 1100, height: 900, show: false });
  await win.loadFile(path.resolve(__dirname, '..', '..', 'docs', 'manual-usuari.html'), { hash: anchor });
  await new Promise((r) => setTimeout(r, 1500));
  fs.mkdirSync(path.dirname(out), { recursive: true });
  fs.writeFileSync(out, (await win.webContents.capturePage()).toPNG());
  console.log('Captura:', out);
  app.quit();
});
