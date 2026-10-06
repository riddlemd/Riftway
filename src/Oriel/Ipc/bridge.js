  // One end of Oriel's contextBridge, shared by the isolated and main worlds. The worlds share only the DOM, so values
  // cross as CustomEvent details, which Chromium structured-clones into the reading world. Functions, promises and
  // DOM nodes cannot be cloned: they cross as markers, functions and promises are proxied, and nodes ride along as a
  // FocusEvent's relatedTarget, the same node in both worlds. Events are dispatched synchronously, so a call returns
  // its value before dispatchEvent does, as Electron's proxied functions do.
  const bridgeEnd = (inbound, outbound, handlers) => {
    const MARK = '__orielBridge';
    const localFunctions = new Map();
    const remoteFunctions = new Map();
    const pendingPromises = new Map();
    const results = new Map();
    const incomingNodes = [];
    let nextId = 1;

    const cloneable = (value) => value instanceof Date || value instanceof RegExp || value instanceof Map || value instanceof Set
      || value instanceof ArrayBuffer || ArrayBuffer.isView(value) || value instanceof Error || value instanceof Blob
      || (typeof ImageData !== 'undefined' && value instanceof ImageData) || (typeof DOMException !== 'undefined' && value instanceof DOMException);

    const encode = (value, nodes, depth = 0) => {
      if (depth > 1000) throw new Error('Object has too much nesting');
      if (typeof value === 'function') {
        const id = nextId++;
        localFunctions.set(id, value);
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
      if (Array.isArray(value)) return value.map((item) => encode(item, nodes, depth + 1));
      // Any other object, a class instance too, crosses as its own string-keyed values; symbols are dropped.
      const copy = {};
      for (const [key, item] of Object.entries(value)) {
        if (typeof item !== 'symbol') copy[key] = encode(item, nodes, depth + 1);
      }
      return copy;
    };

    const decode = (value, nodes, freeze) => {
      if (value === null || typeof value !== 'object' || cloneable(value)) return value;
      if (value[MARK] === 'f') {
        let proxy = remoteFunctions.get(value.id);
        if (!proxy) {
          proxy = (...args) => call(value.id, args);
          remoteFunctions.set(value.id, proxy);
        }
        return freeze ? Object.freeze(proxy) : proxy;
      }
      if (value[MARK] === 'p') return new Promise((resolve, reject) => pendingPromises.set(value.id, { resolve, reject }));
      if (value[MARK] === 'n') return nodes[value.index];
      if (Array.isArray(value)) {
        for (let i = 0; i < value.length; i++) value[i] = decode(value[i], nodes, freeze);
      } else {
        for (const key of Object.keys(value)) value[key] = decode(value[key], nodes, freeze);
      }
      return freeze ? Object.freeze(value) : value;
    };

    function send(message) {
      const nodes = [];
      const detail = { ...message, value: encode(message.value, nodes), args: message.args && encode(message.args, nodes) };
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
      } else if (message.op === 'settle') {
        const pending = pendingPromises.get(message.id);
        pendingPromises.delete(message.id);
        if (message.ok) pending?.resolve(decode(message.value, nodes, false));
        else pending?.reject(decode(message.value, nodes, false));
      } else {
        handlers[message.op]?.(message, (value, freeze) => decode(value, nodes, freeze));
      }
    });

    return { send, call, results };
  };
