// Registered for every page of the default session; only Blazor pages on the app scheme get the channel.
(() => {
  if (location.protocol !== 'app:') return;
  const { ipcRenderer, contextBridge } = oriel;
  contextBridge.exposeInMainWorld('__orielBlazor', {
    send: (message) => ipcRenderer.send('oriel:blazor', message),
    receive: (callback) => { ipcRenderer.on('oriel:blazor', (_event, message) => callback(message)); },
  });
})();
