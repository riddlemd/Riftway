// Reports the page's results to the main process.
oriel.contextBridge.exposeInMainWorld('harness', {
  report: (results) => oriel.ipcRenderer.invoke('report', results),
  progress: (step) => oriel.ipcRenderer.send('progress', step),
  navigated: (search, userAgent) => oriel.ipcRenderer.send('navigated', search, userAgent),
});
