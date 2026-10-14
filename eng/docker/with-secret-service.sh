#!/bin/bash
# Runs a command with a Secret Service on the session bus when ORIEL_SECRET_SERVICE asks for one; otherwise as is.
#   gnome-keyring  an unlocked gnome-keyring-daemon (secrets component): the libsecret path end to end
#   silent         a fake org.freedesktop.secrets whose default collection never answers a property read: the deadlock guard
# Either one also makes the desktop GNOME, which selects gnome-libsecret. Needs the oriel-linux-dev-keyring image.
set -euo pipefail
case "${ORIEL_SECRET_SERVICE:-}" in
  "") exec "$@" ;;
  gnome-keyring)
    # --unlock reads the login keyring's password from stdin and creates the keyring the first time.
    eval "$(printf 'oriel' | gnome-keyring-daemon --unlock --components=secrets)"
    export GNOME_KEYRING_CONTROL ;;
  silent)
    export ORIEL_SS_CALL_LOG="${ORIEL_SS_CALL_LOG:-/tmp/oriel-secret-calls.log}"
    : > "$ORIEL_SS_CALL_LOG"
    python3 "$(dirname "$0")/silent-secret-service.py" &
    ;;
  *) echo "ORIEL_SECRET_SERVICE must be gnome-keyring or silent, not ${ORIEL_SECRET_SERVICE}" >&2; exit 1 ;;
esac
# Whichever service it is, it is up once it owns the name.
for _ in $(seq 1 50); do
  gdbus call --session --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus \
    --method org.freedesktop.DBus.NameHasOwner org.freedesktop.secrets 2>/dev/null | grep -q true && break
  sleep 0.2
done
export XDG_CURRENT_DESKTOP="${XDG_CURRENT_DESKTOP:-GNOME}"
exec "$@"
