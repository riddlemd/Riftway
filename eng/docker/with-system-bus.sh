#!/bin/bash
# Runs a command with DBUS_SYSTEM_BUS_ADDRESS on a private bus of its own: the container has no system bus, and the
# power checks put a fake logind on one. The bus goes with the container.
set -euo pipefail
address=$(dbus-daemon --session --fork --print-address)
DBUS_SYSTEM_BUS_ADDRESS="$address" exec "$@"
