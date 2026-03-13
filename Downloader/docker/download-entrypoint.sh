#!/bin/bash
set -e

/dockerstartup/mount-profile.sh
exec /dockerstartup/download.sh "$@"
