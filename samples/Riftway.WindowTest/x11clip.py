#!/usr/bin/env python3
"""An X11 selection client independent of Riftway, over Xlib through ctypes: reads and owns CLIPBOARD and PRIMARY.

  x11clip.py read SEL TARGET           -> {"ok", "type", "format", "hex"}; INCR transfers are followed
  x11clip.py targets SEL               -> the owner's TARGETS as names
  x11clip.py owner SEL                 -> the owner window's WM_NAME
  x11clip.py multiple SEL T1 T2 ...    -> one MULTIPLE request; {"T1": hex or null, ...}
  x11clip.py own SEL SPEC.json         -> owns SEL and answers requests until another client takes it
SPEC: {"items": [{"target", "type", "hex"}], "targets": bool, "incr": bytes}. "targets": false refuses TARGETS, as
some Java apps do; payloads larger than "incr" go out incrementally.
"""
import ctypes as C, json, select, sys, time

x = C.CDLL("libX11.so.6")
V, U, L, I = C.c_void_p, C.c_ulong, C.c_long, C.c_int
for fn, res, args in [
    ("XOpenDisplay", V, [C.c_char_p]), ("XDefaultRootWindow", U, [V]), ("XCreateSimpleWindow", U, [V, U, I, I, C.c_uint, C.c_uint, C.c_uint, U, U]),
    ("XSelectInput", I, [V, U, L]), ("XInternAtom", U, [V, C.c_char_p, I]), ("XGetAtomName", V, [V, U]), ("XFree", I, [V]),
    ("XConvertSelection", I, [V, U, U, U, U, U]), ("XSetSelectionOwner", I, [V, U, U, U]), ("XGetSelectionOwner", U, [V, U]),
    ("XChangeProperty", I, [V, U, U, U, I, I, V, I]), ("XDeleteProperty", I, [V, U, U]), ("XSendEvent", I, [V, U, I, L, V]),
    ("XGetWindowProperty", I, [V, U, U, L, L, I, U, C.POINTER(U), C.POINTER(I), C.POINTER(U), C.POINTER(U), C.POINTER(V)]),
    ("XPending", I, [V]), ("XNextEvent", I, [V, V]), ("XFlush", I, [V]), ("XConnectionNumber", I, [V]), ("XSync", I, [V, I])]:
    f = getattr(x, fn); f.restype = res; f.argtypes = args
ERROR = C.CFUNCTYPE(I, V, V)(lambda d, e: 0)
x.XSetErrorHandler.argtypes = [C.c_void_p]
x.XSetErrorHandler(ERROR)

d = x.XOpenDisplay(None)
if not d: sys.exit("no display")
w = x.XCreateSimpleWindow(d, x.XDefaultRootWindow(d), 0, 0, 1, 1, 0, 0, 0)
PROPERTY_CHANGE = 1 << 22
x.XSelectInput(d, w, PROPERTY_CHANGE)
ev = (L * 24)()

def atom(n): return x.XInternAtom(d, n.encode("latin-1"), 0)
def name(a):
    if not a: return None
    p = x.XGetAtomName(d, a)
    s = C.string_at(p).decode("latin-1"); x.XFree(p); return s
def field(i): return ev[i] & 0xFFFFFFFFFFFFFFFF

def next_event(deadline):
    while True:
        if x.XPending(d):
            x.XNextEvent(d, ev); return ev[0] & 0xFFFFFFFF
        left = deadline - time.time()
        if left <= 0: return None
        select.select([x.XConnectionNumber(d)], [], [], left)

def get_prop(win, prop):
    t, f, n, after, data = U(), I(), U(), U(), V()
    if x.XGetWindowProperty(d, win, prop, 0, 0x7FFFFFFF, 0, 0, C.byref(t), C.byref(f), C.byref(n), C.byref(after), C.byref(data)) != 0 or f.value == 0:
        return None
    if f.value == 32: raw = b"".join((v & 0xFFFFFFFF).to_bytes(4, "little") for v in C.cast(data, C.POINTER(L))[:n.value])
    elif f.value == 16: raw = C.string_at(data, n.value * 2)
    else: raw = C.string_at(data, n.value)
    x.XFree(data)
    return t.value, f.value, raw

def convert(sel, target, prop_name="RIFTWAY_TEST"):
    prop = atom(prop_name)
    x.XConvertSelection(d, atom(sel), atom(target), prop, w, 0); x.XFlush(d)
    deadline = time.time() + 5
    while True:
        t = next_event(deadline)
        if t is None: return None
        if t == 31 and field(4) == w: break
    if field(7) == 0: return None
    got = get_prop(w, prop); x.XDeleteProperty(d, w, prop); x.XFlush(d)
    if got is None: return None
    if name(got[0]) != "INCR": return got
    chunks, kind, fmt = [], None, 8
    while True:
        t = next_event(time.time() + 5)
        if t is None: return None
        if t == 28 and field(4) == w and field(5) == prop and (ev[7] & 0xFFFFFFFF) == 0:
            part = get_prop(w, prop); x.XDeleteProperty(d, w, prop); x.XFlush(d)
            if part is None: return None
            kind, fmt = part[0], part[1]
            if not part[2]: return kind, fmt, b"".join(chunks)
            chunks.append(part[2])

def result(got):
    return {"ok": got is not None, "type": name(got[0]) if got else None, "format": got[1] if got else None, "hex": got[2].hex() if got else None}

def set_prop(win, prop, kind, fmt, data):
    if fmt == 32:
        values = [int.from_bytes(data[i:i + 4], "little") for i in range(0, len(data), 4)]
        buf = (L * max(1, len(values)))(*values); x.XChangeProperty(d, win, prop, kind, 32, 0, buf, len(values))
    else:
        buf = C.create_string_buffer(data, max(1, len(data))); x.XChangeProperty(d, win, prop, kind, 8, 0, buf, len(data))

def own(sel, spec):
    items = {atom(i["target"]): (atom(i.get("type", i["target"])), bytes.fromhex(i["hex"])) for i in spec["items"]}
    answer_targets, threshold = spec.get("targets", True), spec.get("incr", 1 << 30)
    s = atom(sel); x.XSetSelectionOwner(d, s, w, 0); x.XSync(d, 0)
    if x.XGetSelectionOwner(d, s) != w: sys.exit("not owner")
    print("READY", flush=True)
    transfers = {}
    deadline = time.time() + 120
    while True:
        t = next_event(deadline)
        if t is None: return
        if t == 29 and field(5) == s: print("CLEARED", flush=True); return
        if t == 28 and (ev[7] & 0xFFFFFFFF) == 1 and (field(4), field(5)) in transfers:
            key = (field(4), field(5)); kind, data, off = transfers[key]
            chunk = data[off:off + threshold]; set_prop(key[0], key[1], kind, 8, chunk); x.XFlush(d)
            if chunk: transfers[key] = (kind, data, off + len(chunk))
            else: del transfers[key]
            continue
        if t != 30: continue
        requestor, target, prop, when = field(5), field(7), field(8), field(9)
        ok = True
        if target == atom("TARGETS") and answer_targets:
            set_prop(requestor, prop, atom("ATOM"), 32, b"".join(a.to_bytes(4, "little") for a in [atom("TARGETS"), *items]))
        elif target in items:
            kind, data = items[target]
            if len(data) > threshold:
                set_prop(requestor, prop, atom("INCR"), 32, len(data).to_bytes(4, "little"))
                x.XSelectInput(d, requestor, PROPERTY_CHANGE); transfers[(requestor, prop)] = (kind, data, 0)
            else: set_prop(requestor, prop, kind, 8, data)
        else: ok = False
        reply = (L * 24)()
        reply[0], reply[2], reply[3], reply[4], reply[5], reply[6], reply[7], reply[8] = 31, 1, d, requestor, s, target, prop if ok else 0, when
        x.XSendEvent(d, requestor, 0, 0, reply); x.XFlush(d)

cmd, sel = sys.argv[1], sys.argv[2]
if cmd == "read": print(json.dumps(result(convert(sel, sys.argv[3]))))
elif cmd == "targets":
    got = convert(sel, "TARGETS")
    print(json.dumps([name(int.from_bytes(got[2][i:i + 4], "little")) for i in range(0, len(got[2]), 4)] if got else None))
elif cmd == "owner":
    owner = x.XGetSelectionOwner(d, atom(sel))
    got = get_prop(owner, atom("WM_NAME")) if owner else None
    print(json.dumps(got[2].decode("latin-1") if got else None))
elif cmd == "multiple":
    pairs = [(atom(t), atom("RIFTWAY_M%d" % i)) for i, t in enumerate(sys.argv[3:])]
    set_prop(w, atom("RIFTWAY_PAIRS"), atom("ATOM_PAIR"), 32, b"".join(a.to_bytes(4, "little") + p.to_bytes(4, "little") for a, p in pairs))
    got = convert(sel, "MULTIPLE", "RIFTWAY_PAIRS")
    out = {}
    if got:
        answered = [int.from_bytes(got[2][i:i + 4], "little") for i in range(0, len(got[2]), 4)]
        for (t, p), i in zip(pairs, range(1, len(answered), 2)):
            part = get_prop(w, p) if answered[i] else None
            out[name(t)] = part[2].hex() if part else None
    print(json.dumps(out))
elif cmd == "own": own(sel, json.load(open(sys.argv[3])))
