#!/bin/sh
# Gives the server a credential store before it starts, then gets out of the way.
#
# The chain in ClrKernel.Core.Secrets prefers the OS store over everything else, so
# a running Secret Service is all it takes for saved passwords to land in an
# encrypted keyring rather than anywhere else. Without one, nothing can be saved
# and passwords have to arrive as CLRKERNEL_SECRET_* variables — which is the
# honest fallback, not a silent downgrade to a file anyone with the volume can read.
set -e

keyring_password() {
    if [ -n "$CLRKERNEL_STUDIO_KEYRING_PASSWORD_FILE" ]; then
        # The form docker and kubernetes secrets take. Preferred over the variable
        # below, which is visible in `docker inspect` and in the environment of
        # every process this one spawns, the kernel included.
        if [ -r "$CLRKERNEL_STUDIO_KEYRING_PASSWORD_FILE" ]; then
            cat "$CLRKERNEL_STUDIO_KEYRING_PASSWORD_FILE"
            return 0
        fi
        echo "clrkernel-studio: $CLRKERNEL_STUDIO_KEYRING_PASSWORD_FILE cannot be read." >&2
        return 1
    fi
    [ -n "$CLRKERNEL_STUDIO_KEYRING_PASSWORD" ] || return 1
    printf '%s' "$CLRKERNEL_STUDIO_KEYRING_PASSWORD"
}

# 0 unlocked · 1 no password was given · 2 a password was given and did not work.
start_keyring() {
    password="$(keyring_password)" || return 1

    # The keyring lives on the data volume, so it survives the container. It is
    # encrypted with the password above, which deliberately does not live there —
    # a key kept beside the thing it locks is decoration.
    XDG_DATA_HOME="${CLRKERNEL_STUDIO_DATA:-/data}/keyring"
    export XDG_DATA_HOME
    mkdir -p "$XDG_DATA_HOME"

    # A session bus, forked rather than wrapped around the app: the app has to stay
    # PID 1 so `docker stop` reaches it and the scheduler drains its runs instead of
    # being killed with them.
    DBUS_SESSION_BUS_ADDRESS="$(dbus-daemon --session --fork --print-address)"
    export DBUS_SESSION_BUS_ADDRESS

    # --unlock reads the password from stdin and creates the keyring the first time.
    # A wrong password fails here rather than at the first lookup.
    printf '%s' "$password" | gnome-keyring-daemon --unlock --daemonize > "$_env_file.tmp" 2>/dev/null || {
        echo "clrkernel-studio: the keyring daemon would not start." >&2
        return 2
    }
    while IFS= read -r line; do
        [ -n "$line" ] && export "$line"
    done < "$_env_file.tmp"

    # Started is not unlocked. A password that does not match an existing keyring
    # leaves the collection locked, and gnome-keyring says so only when something
    # tries to write — so try, here, rather than letting every saved password go
    # missing at once with the cause a container start away.
    if ! printf 'x' | secret-tool store --label probe service ClrKernel account __startup__ 2>/dev/null; then
        echo "clrkernel-studio: the keyring is locked — the password does not match the one" >&2
        echo "clrkernel-studio: this keyring was created with. Saved passwords will not resolve." >&2
        return 2
    fi
    secret-tool clear service ClrKernel account __startup__ 2>/dev/null || true

    # Written down so `docker exec` can join the same session — it inherits none of
    # this, and a one-shot `run` that cannot see the keyring would fail to resolve
    # exactly the passwords the scheduler resolves fine.
    {
        echo "export XDG_DATA_HOME='$XDG_DATA_HOME'"
        echo "export DBUS_SESSION_BUS_ADDRESS='$DBUS_SESSION_BUS_ADDRESS'"
        sed 's/^/export /' "$_env_file.tmp"
    } > "$_env_file"
    rm -f "$_env_file.tmp"
    return 0
}

_env_file=/tmp/clrkernel-keyring.env

start_keyring && _keyring=0 || _keyring=$?
if [ "$_keyring" = 0 ]; then
    echo "clrkernel-studio: keyring unlocked; saved passwords are kept in it." >&2
else
    rm -f "$_env_file" "$_env_file.tmp"
    if [ "$_keyring" = 1 ]; then
        echo "clrkernel-studio: no keyring (set CLRKERNEL_STUDIO_KEYRING_PASSWORD_FILE to get one)." >&2
    fi
    echo "clrkernel-studio: passwords cannot be saved from the web app; supply them as" >&2
    echo "clrkernel-studio: CLRKERNEL_SECRET_* variables instead." >&2
fi

# Only for the migration: an older image kept saved passwords in this file. The
# server moves them into the keyring on startup and deletes it. Pointed at only
# when it is there, so a fresh install never gains a plaintext store.
if [ -f "${CLRKERNEL_STUDIO_DATA:-/data}/secrets.json" ]; then
    CLRKERNEL_SECRETS_FILE="${CLRKERNEL_STUDIO_DATA:-/data}/secrets.json"
    export CLRKERNEL_SECRETS_FILE
fi

# exec, so the server is PID 1 and takes SIGTERM itself.
exec /app/studio/ClrKernel.Studio "$@"
