// Oriel's navigator.permissions.query(): the state comes from the session's permission check handler, as Electron's
// does. Runs in the page's own world before its scripts. Chromium's query still runs first, so a bad descriptor
// rejects as it would, and the page keeps a real PermissionStatus.
(() => {
  // A DevTools binding; deleted so the page cannot reach it under this name.
  const ask = globalThis.__orielPermissionCheck;
  delete globalThis.__orielPermissionCheck;
  if (typeof ask !== 'function' || typeof Permissions === 'undefined') return;

  const native = Permissions.prototype.query;
  const pending = new Map();
  let nextId = 1;

  // C# answers through Runtime.evaluate in this context; null keeps Chromium's state.
  Object.defineProperty(globalThis, Symbol.for('oriel.permissionCheck'), {
    value: (id, state) => {
      const settle = pending.get(id);
      pending.delete(id);
      settle?.(state);
    },
  });

  const { query } = {
    query(descriptor) {
      return native.call(this, descriptor).then((status) => new Promise((resolve) => {
        const id = nextId++;
        pending.set(id, (state) => {
          if (state) Object.defineProperty(status, 'state', { value: state, enumerable: true, configurable: true });
          resolve(status);
        });
        ask(JSON.stringify({
          id,
          name: String(descriptor.name),
          sysex: !!descriptor.sysex,
          allowWithoutSanitization: !!descriptor.allowWithoutSanitization,
          url: location.href,
          mainFrame: globalThis === globalThis.top,
        }));
      }));
    },
  };
  Object.defineProperty(Permissions.prototype, 'query', { value: query, writable: true, enumerable: true, configurable: true });
})();
