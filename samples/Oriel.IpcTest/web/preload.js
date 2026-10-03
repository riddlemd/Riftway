// The app preload: runs in Oriel's isolated world and exposes a narrow API to the page.
const { ipcRenderer, contextBridge } = oriel;

contextBridge.exposeInMainWorld('api', {
  version: 1,
  ping: (text) => ipcRenderer.invoke('ping', text),
  add: (a, b) => ipcRenderer.invoke('add', a, b),
  fail: () => ipcRenderer.invoke('fail'),
  unhandled: () => ipcRenderer.invoke('no-such-channel'),
  onTick: (callback) => {
    ipcRenderer.on('tick', (_event, value) => callback(value));
    ipcRenderer.send('subscribe');
  },
  report: (results) => ipcRenderer.invoke('report', results),
});
