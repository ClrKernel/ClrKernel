#!/bin/sh
# `docker exec` starts outside the entrypoint's session, so it sees no keyring.
# This joins it, then runs whatever you asked for:
#
#   docker exec <container> clrkernel-studio new-admin-invite
#   docker exec <container> clrkernel-studio run nightly
set -e
[ -f /tmp/clrkernel-keyring.env ] && . /tmp/clrkernel-keyring.env
exec /app/studio/ClrKernel.Studio "$@"
