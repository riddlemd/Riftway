// Oriel's preload runtime. Runs in the isolated world "oriel" before any page script, so nothing
// defined here is reachable from the page; the app's own preload runs after it in the same world.
(() => {
  // A DevTools binding exposed to this world only; deleted so even the app preload cannot bypass ipcRenderer.
  const post = globalThis.__orielIpc;
  delete globalThis.__orielIpc;

  // Declared by the script around this runtime, so its preload wrappers can report failures as Electron's init does.
  reportPreloadError = (path, error) => post(JSON.stringify({
    t: 'preload-error', ch: '',
    args: [path, { name: String(error?.name ?? 'Error'), message: String(error?.message ?? error), stack: String(error?.stack ?? '') }],
  }));

  // A click into the page puts an Alt-shown menu bar away (Electron's). Captured, so the page cannot stop it.
  globalThis.addEventListener('mousedown', () => post(JSON.stringify({ t: 'mousedown', ch: '', args: [] })), true);

  // The frame that last took focus is webContents.focusedFrame. Captured: an element's focus does not bubble.
  globalThis.addEventListener('focus', () => post(JSON.stringify({ t: 'focus', ch: '', args: [] })), true);

  // Ports bridged to the main process: this world holds one end of each under an id, and relays it.
  const bridged = new Map();
  const hold = (id, port) => {
    bridged.set(id, port);
    port.onmessage = (event) =>
      post(JSON.stringify({ t: 'port-msg', id, ch: '', args: [], data: event.data, ports: event.ports.map(adopt) }));
    port.addEventListener('close', () => {
      if (bridged.delete(id)) post(JSON.stringify({ t: 'port-close', id, ch: '', args: [] }));
    });
  };
  // A port this page made, moving to the main process.
  const adopt = (port) => {
    const id = crypto.randomUUID();
    hold(id, port);
    return id;
  };
  // A port arriving from the main process: the page gets one end of a new channel; this world keeps the other.
  const bridge = (id) => {
    const channel = new MessageChannel();
    hold(id, channel.port2);
    return channel.port1;
  };

  // What webFrame answers at once: the main process reports both, and Oriel's own zoom changes.
  let frameToken = '';
  let zoomLevel = 0;

  let nextInvokeId = 1;
  const pendingInvokes = new Map();
  const channelListeners = new Map();

  // C# calls this through Runtime.evaluate in this world's context.
  Object.defineProperty(globalThis, '__orielDeliver', {
    value: (message) => {
      if (message.t === 'reply') {
        const pending = pendingInvokes.get(message.id);
        if (!pending) return;
        pendingInvokes.delete(message.id);
        if (message.ok) pending.resolve(message.value);
        else pending.reject(new Error(message.error));
      } else if (message.t === 'frame') {
        frameToken = message.token;
        zoomLevel = message.zoomLevel;
      } else if (message.t === 'zoom') {
        zoomLevel = message.level;
      } else if (message.t === 'port-msg') {
        bridged.get(message.id)?.postMessage(message.data, (message.ports ?? []).map(bridge));
      } else if (message.t === 'port-close') {
        const port = bridged.get(message.id);
        bridged.delete(message.id);
        port?.close();
      } else if (message.t === 'msg') {
        const event = { sender: ipcRenderer, ports: (message.ports ?? []).map(bridge) };
        for (const listener of [...(channelListeners.get(message.ch) ?? [])]) listener(event, ...message.args);
      }
    },
  });

  const ipcRenderer = Object.freeze({
    invoke(channel, ...args) {
      const id = nextInvokeId++;
      return new Promise((resolve, reject) => {
        pendingInvokes.set(id, { resolve, reject });
        post(JSON.stringify({ t: 'invoke', id, ch: String(channel), args }));
      });
    },
    send(channel, ...args) {
      post(JSON.stringify({ t: 'send', ch: String(channel), args }));
    },
    postMessage(channel, message, transfer = []) {
      post(JSON.stringify({ t: 'post', ch: String(channel), args: [message], ports: transfer.map(adopt) }));
    },
    on(channel, listener) {
      if (!channelListeners.has(channel)) channelListeners.set(channel, new Set());
      channelListeners.get(channel).add(listener);
      return ipcRenderer;
    },
    // A once wrapper remembers its listener, so removeListener(channel, listener) also removes it.
    once(channel, listener) {
      const wrapper = (...args) => {
        ipcRenderer.removeListener(channel, wrapper);
        listener(...args);
      };
      wrapper.listener = listener;
      return ipcRenderer.on(channel, wrapper);
    },
    removeListener(channel, listener) {
      const listeners = channelListeners.get(channel);
      if (!listeners) return ipcRenderer;
      for (const registered of [...listeners]) {
        if (registered === listener || registered.listener === listener) {
          listeners.delete(registered);
          break;
        }
      }
      return ipcRenderer;
    },
    off(channel, listener) {
      return ipcRenderer.removeListener(channel, listener);
    },
    removeAllListeners(channel) {
      if (channel === undefined) channelListeners.clear();
      else channelListeners.delete(channel);
      return ipcRenderer;
    },
  });

  // Worlds share only the DOM. Exposures and calls cross as synchronous CustomEvents with string
  // details (objects from another world arrive as null), so an exposure exists before page scripts run.
  const exposed = new Map();
  const fire = (type, data) => document.dispatchEvent(new CustomEvent(type, { detail: JSON.stringify(data) }));

  document.addEventListener('oriel-bridge-call', (event) => {
    const { callId, key, method, args } = JSON.parse(event.detail);
    const target = exposed.get(key)?.[method];
    const decoded = args.map((arg) =>
      arg && typeof arg === 'object' && '__orielCallback' in arg
        ? (...callbackArgs) => fire('oriel-bridge-callback', { id: arg.__orielCallback, args: callbackArgs })
        : arg);

    Promise.resolve()
      .then(() => {
        if (typeof target !== 'function') throw new Error(`${key}.${method} is not exposed`);
        return target(...decoded);
      })
      .then(
        (value) => fire('oriel-bridge-result', { callId, ok: true, value }),
        (error) => fire('oriel-bridge-result', { callId, ok: false, error: String(error?.message ?? error) }));
  });

  const contextBridge = Object.freeze({
    exposeInMainWorld(key, api) {
      const methods = [];
      const values = {};
      for (const [name, value] of Object.entries(api)) {
        if (typeof value === 'function') methods.push(name);
        else values[name] = value;
      }
      exposed.set(key, api);
      fire('oriel-bridge-expose', { key, methods, values });
    },
  });

  const webFrameCall = (op, data) => new Promise((resolve, reject) => {
    const id = nextInvokeId++;
    pendingInvokes.set(id, { resolve, reject });
    post(JSON.stringify({ t: 'wf', id, op, ch: '', args: [], ...data }));
  });
  let nextCssKey = 0;
  const cssKeys = new Map();
  // Electron's renderer webFrame, for this frame; the work happens in the main process over DevTools.
  const webFrame = Object.freeze({
    setZoomFactor(factor) {
      if (!(factor > 0)) throw new Error('factor must be greater than 0.0');
      webFrame.setZoomLevel(Math.log(factor) / Math.log(1.2));
    },
    getZoomFactor: () => Math.pow(1.2, zoomLevel),
    setZoomLevel(level) {
      zoomLevel = Number(level);
      webFrameCall('zoomLevel', { value: zoomLevel });
    },
    getZoomLevel: () => zoomLevel,
    insertCSS(css, options = {}) {
      const key = String(++nextCssKey);
      const created = webFrameCall('insertCSS', { css: String(css), origin: options.cssOrigin ?? 'author' });
      created.catch(() => {});
      cssKeys.set(key, created);
      return key;
    },
    removeInsertedCSS(key) {
      const created = cssKeys.get(key);
      if (!created) return;
      cssKeys.delete(key);
      created.then((sheet) => webFrameCall('removeCSS', { key: sheet }), () => {});
    },
    insertText(text) {
      webFrameCall('insertText', { text: String(text) });
    },
    executeJavaScript(code, userGesture = false, callback) {
      const result = webFrameCall('exec', { code: String(code), userGesture: !!userGesture });
      if (typeof callback === 'function') result.then((value) => callback(value, null), (error) => callback(null, error));
      return result;
    },
    executeJavaScriptInIsolatedWorld(worldId, scripts, userGesture = false, callback) {
      const result = webFrameCall('execIsolated', {
        worldId: Number(worldId), userGesture: !!userGesture,
        scripts: scripts.map((s) => ({ code: String(s.code), url: s.url === undefined ? undefined : String(s.url) })),
      });
      if (typeof callback === 'function') result.then((value) => callback(value, null), (error) => callback(null, error));
      return result;
    },
    get frameToken() { return frameToken; },
  });

  Object.defineProperty(globalThis, 'oriel', { value: Object.freeze({ ipcRenderer, contextBridge, webFrame }) });
})();
