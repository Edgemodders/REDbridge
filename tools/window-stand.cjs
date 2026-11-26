// A stand-in window for tools/window-check.mjs, run by Electron. role owner: a "VS Code" window whose
// page (the webview) is titled by `page`; role target: a framed "game" window. Commands on stdin,
// one JSON per line: {move:[x,y]}, {page:"title"}, {quit:true}. Prints {ready, pid} once shown.
const { app, BrowserWindow } = require('electron');
const role = process.env.RB22_ROLE;
app.whenReady().then(() => {
  const owner = role === 'owner';
  const win = new BrowserWindow({
    x: owner ? 120 : 1400, y: owner ? 120 : 600, width: owner ? 1100 : 420, height: owner ? 760 : 320,
    title: owner ? 'rb22-owner' : 'rb22-target', backgroundColor: owner ? '#202830' : '#a03020', show: true,
  });
  win.on('page-title-updated', (e) => e.preventDefault()); // keep the window's own title
  const page = (t) => win.loadURL('data:text/html,' + encodeURIComponent(`<title>${t}</title><body style="background:${owner ? '#203040' : '#802010'};color:#fff">${t}</body>`));
  page(owner ? 'rb22-webview' : 'rb22-game');
  win.webContents.once('did-finish-load', () => process.stdout.write(JSON.stringify({ ready: true, pid: process.pid }) + '\n'));
  // Commands from a control file (Electron's main process does not read stdin on Windows).
  const fs = require('fs');
  const file = process.env.RB22_CONTROL;
  let done = 0;
  setInterval(() => {
    let lines = [];
    try { lines = fs.readFileSync(file, 'utf8').split('\n').filter(Boolean); } catch {}
    for (; done < lines.length; done++) {
      const m = JSON.parse(lines[done]);
      if (m.move) win.setPosition(m.move[0], m.move[1]);
      if (m.page) page(m.page);
      if (m.quit) app.quit();
      if (m.hang) { const end = Date.now() + m.hang; while (Date.now() < end) { /* the main thread stops answering */ } }
    }
  }, 100);
});
