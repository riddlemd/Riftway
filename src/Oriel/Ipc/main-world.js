// Oriel's main-world half of contextBridge. Runs in the page's own world before its scripts and
// turns exposures from the isolated preload into globals whose methods return promises.
(() => {
  let nextCallId = 1;
  let nextCallbackId = 1;
  const pendingCalls = new Map();
  const callbacks = new Map();
  const fire = (type, data) => document.dispatchEvent(new CustomEvent(type, { detail: JSON.stringify(data) }));

  // Functions cannot cross worlds, so callback arguments become ids the preload calls back through.
  const encode = (args) => args.map((arg) => {
    if (typeof arg !== 'function') return arg;
    const id = nextCallbackId++;
    callbacks.set(id, arg);
    return { __orielCallback: id };
  });

  document.addEventListener('oriel-bridge-expose', (event) => {
    const { key, methods, values } = JSON.parse(event.detail);
    const api = { ...values };
    for (const method of methods) {
      api[method] = (...args) => new Promise((resolve, reject) => {
        const callId = nextCallId++;
        pendingCalls.set(callId, { resolve, reject });
        fire('oriel-bridge-call', { callId, key, method, args: encode(args) });
      });
    }
    Object.defineProperty(window, key, { value: Object.freeze(api), enumerable: true });
  });

  document.addEventListener('oriel-bridge-result', (event) => {
    const { callId, ok, value, error } = JSON.parse(event.detail);
    const pending = pendingCalls.get(callId);
    if (!pending) return;
    pendingCalls.delete(callId);
    if (ok) pending.resolve(value);
    else pending.reject(new Error(error));
  });

  document.addEventListener('oriel-bridge-callback', (event) => {
    const { id, args } = JSON.parse(event.detail);
    callbacks.get(id)?.(...args);
  });
})();
