#!/bin/bash
set -e

# Run the profile image mount as root
/dockerstartup/mount-profile.sh

# Hand off to Kasm's normal startup chain
exec /dockerstartup/kasm_default_profile.sh \
     /dockerstartup/vnc_startup.sh \
     /dockerstartup/kasm_startup.sh "$@"
