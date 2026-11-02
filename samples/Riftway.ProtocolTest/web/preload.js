// Reports the page's results to the main process.
riftway.contextBridge.exposeInMainWorld('harness', {
  report: (results) => riftway.ipcRenderer.invoke('report', results),
  progress: (step) => riftway.ipcRenderer.send('progress', step),
  navigated: (search, userAgent) => riftway.ipcRenderer.send('navigated', search, userAgent),
});
