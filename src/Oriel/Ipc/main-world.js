// Oriel's main-world half of contextBridge. Runs in the page's own world before its scripts, so an exposure exists
// before any page script runs.
(() => {
/*@bridge*/
  const end = bridgeEnd('oriel-bridge-to-main', 'oriel-bridge-to-isolated', {
    expose(message, decode) {
      if (message.key in window) {
        end.send({ op: 'return', callId: message.callId, ok: false, value: new Error('Cannot bind an API on top of an existing property on the window object') });
        return;
      }
      Object.defineProperty(window, message.key, { value: decode(message.value, true), enumerable: true });
      end.send({ op: 'return', callId: message.callId, ok: true, value: undefined });
    },
    // contextBridge.executeInMainWorld: the function arrives as its source, as Electron serializes it.
    exec(message, decode) {
      try {
        const func = (0, eval)(`(${message.source})`);
        end.send({ op: 'return', callId: message.callId, ok: true, value: func(...decode(message.args, false)) });
      } catch (error) {
        end.send({ op: 'return', callId: message.callId, ok: false, value: error });
      }
    },
  });
})();
