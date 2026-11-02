// The app preload: runs in Riftway's isolated world and exposes a narrow API to the page.
const { ipcRenderer, contextBridge } = riftway;

// Lifetime probes: a FinalizationRegistry sees an original only once nothing, the bridge included, holds it.
const collected = new Set();
const watched = new FinalizationRegistry((tag) => collected.add(tag));
let held = null;
let kept = null;
let last = null;
let lastObject = null;
let settleLater = null;

contextBridge.exposeInMainWorld('api', {
  version: 1,
  ping: (text) => ipcRenderer.invoke('ping', text),
  add: (a, b) => ipcRenderer.invoke('add', a, b),
  fail: () => ipcRenderer.invoke('fail'),
  unhandled: () => ipcRenderer.invoke('no-such-channel'),
  onTick: (callback) => {
    ipcRenderer.on('tick', (_event, value) => callback(value));
    ipcRenderer.send('subscribe');
  },
  report: (results) => ipcRenderer.invoke('report', results),

  hold: (fn) => { held = fn; },
  drop: () => { held = null; },
  keep: (fn) => { kept = fn; },
  callKept: (value) => kept(value),
  make: (tag) => {
    const made = () => 'made:' + tag;
    watched.register(made, tag);
    return made;
  },
  collected: (tag) => collected.has(tag),
  same: (a, b) => a === b,
  echo: (fn) => fn,
  sameAsLast: (fn) => {
    const same = fn === last;
    last = fn;
    return same;
  },
  forgetLast: () => { last = null; },

  shapeOf: (value) => ({
    shared: value.a === value.b,
    cyclic: value.self === value,
    arrayShared: Array.isArray(value.list) && value.list[0] === value.list[1],
  }),
  makeCycle: () => {
    const made = { name: 'made', inner: {} };
    made.self = made;
    made.twice = [made.inner, made.inner];
    return made;
  },
  sameAsLastObject: (value) => {
    const same = value === lastObject;
    lastObject = value;
    return same;
  },
  never: () => new Promise(() => {}),
  later: () => new Promise((resolve) => { settleLater = resolve; }),
  settle: (value) => settleLater(value),
});
