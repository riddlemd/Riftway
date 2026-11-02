#!/usr/bin/env python3
# A Secret Service that owns org.freedesktop.secrets and names a default collection, then answers nothing else: the
# deadlocked gnome-keyring that Chromium's CanUseLibsecret and FreedesktopSecretKeyProvider detect with
# Properties.Get(Collection, "Label"), and in which every libsecret call waits out its D-Bus timeout.
import os

from gi.repository import Gio, GLib

COLLECTION = "/org/freedesktop/secrets/aliases/default"
XML = """
<node>
  <interface name="org.freedesktop.Secret.Service">
    <method name="ReadAlias"><arg type="s" direction="in"/><arg type="o" direction="out"/></method>
    <method name="OpenSession"><arg type="s" direction="in"/><arg type="v" direction="in"/><arg type="v" direction="out"/><arg type="o" direction="out"/></method>
    <method name="SearchItems"><arg type="a{ss}" direction="in"/><arg type="ao" direction="out"/><arg type="ao" direction="out"/></method>
  </interface>
  <interface name="org.freedesktop.DBus.Properties">
    <method name="Get"><arg type="s" direction="in"/><arg type="s" direction="in"/><arg type="v" direction="out"/></method>
    <method name="GetAll"><arg type="s" direction="in"/><arg type="a{sv}" direction="out"/></method>
  </interface>
</node>
"""
CALL_LOG = os.environ.get("RIFTWAY_SS_CALL_LOG")
held = []  # Unanswered calls stay referenced, so the caller times out rather than getting an error.


def on_call(connection, sender, path, interface, method, parameters, invocation):
    # The log tells a guard that stopped at the Label read from libsecret waiting out the same timeout itself.
    if CALL_LOG:
        with open(CALL_LOG, "a") as log:
            log.write(f"{interface}.{method}\n")
    if method == "ReadAlias":
        invocation.return_value(GLib.Variant("(o)", (COLLECTION,)))
    else:
        held.append(invocation)


def on_bus(connection, name):
    info = Gio.DBusNodeInfo.new_for_xml(XML)
    for path in ("/org/freedesktop/secrets", COLLECTION):
        for interface in info.interfaces:
            connection.register_object(path, interface, on_call, None, None)


Gio.bus_own_name(Gio.BusType.SESSION, "org.freedesktop.secrets", Gio.BusNameOwnerFlags.NONE, on_bus, None, None)
GLib.MainLoop().run()
