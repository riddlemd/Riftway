// Echoes on the port the main process hands over, so a utility process can talk to this page through it.
const { ipcRenderer } = riftway;

ipcRenderer.on('child-port', (event) => {
  const [port] = event.ports;
  port.onmessage = (e) => port.postMessage('page:' + e.data);
});
ipcRenderer.send('page-ready');
