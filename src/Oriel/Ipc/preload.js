// Oriel's preload runtime. Runs in the isolated world "oriel" before any page script, so nothing
// defined here is reachable from the page; the app's own preload runs after it in the same world.
(() => {
  // A DevTools binding exposed to this world only; deleted so even the app preload cannot bypass ipcRenderer.
  const post = globalThis.__orielIpc;
  delete globalThis.__orielIpc;

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
      } else if (message.t === 'msg') {
        const event = { sender: ipcRenderer };
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

  Object.defineProperty(globalThis, 'oriel', { value: Object.freeze({ ipcRenderer, contextBridge }) });
})();
