  // One end of Oriel's contextBridge, shared by the isolated and main worlds. The worlds share only the DOM, so values
  // cross as CustomEvent details, which Chromium structured-clones into the reading world. Functions, promises and
  // DOM nodes cannot be cloned: they cross as markers, functions and promises are proxied, and nodes ride along as a
  // FocusEvent's relatedTarget, the same node in both worlds. Events are dispatched synchronously, so a call returns
  // its value before dispatchEvent does, as Electron's proxied functions do.
  const bridgeEnd = (inbound, outbound, handlers) => {
    const MARK = '__orielBridge';
    // A proxy holds its original only through the other world's localFunctions entry, so the entry lives until the
    // proxy is collected, as Electron's proxy holds its original in its own V8 function data.
    const localFunctions = new Map();
    const remoteFunctions = new Map();
    const proxyIds = new WeakMap();
    const proxies = new FinalizationRegistry((id) => {
      remoteFunctions.delete(id);
      send({ op: 'release', id });
    });
    // A proxied promise is held only by its original's pending then: its resolvers live as long as it does, keyed by it.
    const pendingPromises = new Map();
    const resolvers = new WeakMap();
    const abandoned = new FinalizationRegistry((id) => pendingPromises.delete(id));
    const results = new Map();
    const incomingNodes = [];
    let nextId = 1;

    const cloneable = (value) => value instanceof Date || value instanceof RegExp || value instanceof Map || value instanceof Set
      || value instanceof ArrayBuffer || ArrayBuffer.isView(value) || value instanceof Error || value instanceof Blob
      || (typeof ImageData !== 'undefined' && value instanceof ImageData) || (typeof DOMException !== 'undefined' && value instanceof DOMException);

    // `functions` and `objects` are one crossing's cache, as Electron's ObjectCache: a function gets one proxy and an
    // object one copy per crossing, so a repeated or cyclic object keeps its shape, which the structured clone carries over.
    const encode = (value, nodes, functions, objects, depth = 0) => {
      if (depth > 1000) throw new Error('Object has too much nesting');
      if (typeof value === 'function') {
        // A proxy going back to its original's world arrives as the original (Electron's kOriginalFunctionPrivateKey).
        const original = proxyIds.get(value);
        if (original !== undefined) return { [MARK]: 'o', id: original };
        let id = functions.get(value);
        if (id === undefined) {
          id = nextId++;
          functions.set(value, id);
          localFunctions.set(id, value);
        }
        return { [MARK]: 'f', id };
      }
      if (value === null || typeof value !== 'object') return value;
      if (value instanceof Promise) {
        const id = nextId++;
        value.then(
          (settled) => send({ op: 'settle', id, ok: true, value: settled }),
          (reason) => send({ op: 'settle', id, ok: false, value: reason }));
        return { [MARK]: 'p', id };
      }
      if (value instanceof Node) {
        nodes.push(value);
        return { [MARK]: 'n', index: nodes.length - 1 };
      }
      if (cloneable(value)) return value;
      const known = objects.get(value);
      if (known) return known;
      if (Array.isArray(value)) {
        const copy = new Array(value.length);
        objects.set(value, copy);
        value.forEach((item, i) => { copy[i] = encode(item, nodes, functions, objects, depth + 1); });
        return copy;
      }
      // Any other object, a class instance too, crosses as its own string-keyed values; symbols are dropped.
      const copy = {};
      objects.set(value, copy);
      for (const [key, item] of Object.entries(value)) {
        if (typeof item !== 'symbol') copy[key] = encode(item, nodes, functions, objects, depth + 1);
      }
      return copy;
    };

    const decode = (value, nodes, freeze, seen = new Set()) => {
      if (value === null || typeof value !== 'object' || cloneable(value)) return value;
      if (seen.has(value)) return value;
      if (value[MARK] === 'o') return freeze ? Object.freeze(localFunctions.get(value.id)) : localFunctions.get(value.id);
      if (value[MARK] === 'f') {
        const id = value.id;
        let proxy = remoteFunctions.get(id)?.deref();
        if (!proxy) {
          proxy = (...args) => call(id, args);
          remoteFunctions.set(id, new WeakRef(proxy));
          proxyIds.set(proxy, id);
          proxies.register(proxy, id);
        }
        return freeze ? Object.freeze(proxy) : proxy;
      }
      if (value[MARK] === 'p') {
        let settle;
        const promise = new Promise((resolve, reject) => { settle = { resolve, reject }; });
        resolvers.set(promise, settle);
        pendingPromises.set(value.id, new WeakRef(promise));
        abandoned.register(promise, value.id);
        return promise;
      }
      if (value[MARK] === 'n') return nodes[value.index];
      seen.add(value);
      if (Array.isArray(value)) {
        for (let i = 0; i < value.length; i++) value[i] = decode(value[i], nodes, freeze, seen);
      } else {
        for (const key of Object.keys(value)) value[key] = decode(value[key], nodes, freeze, seen);
      }
      return freeze ? Object.freeze(value) : value;
    };

    function send(message) {
      const nodes = [];
      const functions = new Map();
      const objects = new Map();
      const detail = { ...message, value: encode(message.value, nodes, functions, objects), args: message.args && encode(message.args, nodes, functions, objects) };
      for (const node of nodes) document.dispatchEvent(new FocusEvent(outbound + '-node', { relatedTarget: node }));
      detail.nodes = nodes.length;
      document.dispatchEvent(new CustomEvent(outbound, { detail }));
    }

    function call(id, args) {
      const callId = nextId++;
      send({ op: 'call', callId, id, args });
      const result = results.get(callId);
      results.delete(callId);
      if (!result) throw new Error('The function on the other side of the context bridge did not answer');
      if (!result.ok) throw result.value;
      return result.value;
    }

    document.addEventListener(inbound + '-node', (event) => incomingNodes.push(event.relatedTarget));
    document.addEventListener(inbound, (event) => {
      const message = event.detail;
      if (!message || typeof message !== 'object') return;
      const nodes = incomingNodes.splice(0, message.nodes ?? 0);
      if (message.op === 'call') {
        const target = localFunctions.get(message.id);
        try {
          if (!target) throw new Error('This function has gone from the other side of the context bridge');
          send({ op: 'return', callId: message.callId, ok: true, value: target(...decode(message.args, nodes, false)) });
        } catch (error) {
          send({ op: 'return', callId: message.callId, ok: false, value: error });
        }
      } else if (message.op === 'return') {
        results.set(message.callId, { ok: message.ok, value: decode(message.value, nodes, false) });
      } else if (message.op === 'release') {
        localFunctions.delete(message.id);
      } else if (message.op === 'settle') {
        const pending = resolvers.get(pendingPromises.get(message.id)?.deref());
        pendingPromises.delete(message.id);
        if (message.ok) pending?.resolve(decode(message.value, nodes, false));
        else pending?.reject(decode(message.value, nodes, false));
      } else {
        handlers[message.op]?.(message, (value, freeze) => decode(value, nodes, freeze));
      }
    });

    return { send, call, results };
  };
