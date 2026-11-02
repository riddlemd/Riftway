// Registered for every page of the default session; only Blazor pages on the app scheme get the channel.
(() => {
  if (location.protocol !== 'app:') return;
  const { ipcRenderer, contextBridge } = riftway;
  contextBridge.exposeInMainWorld('__riftwayBlazor', {
    send: (message) => ipcRenderer.send('riftway:blazor', message),
    receive: (callback) => { ipcRenderer.on('riftway:blazor', (_event, message) => callback(message)); },
  });
})();
