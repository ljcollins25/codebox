#!/usr/bin/env bash
# Usage: bench.sh <mode> <parallel> <input file> [part size]   (env: R2PIPE_URL, R2PIPE_TOKEN or Access service token)
# Runs a send and a recv at the same time on this machine (the data goes out to R2 and back) and prints one JSON line.
set -euo pipefail
MODE=$1; PAR=$2; IN=$3; PS=${4:-32M}
R2PIPE=${R2PIPE_BIN:-r2pipe}
W=$(mktemp -d); trap 'rm -rf "$W"' EXIT
T0=$(date +%s%3N)
$R2PIPE send "$IN" --mode "$MODE" --parallel "$PAR" --part-size "$PS" ${EXTRA:-} --quiet --stats "$W/send.json" >"$W/id" 2>"$W/send.err" &
SP=$!
for i in $(seq 1 100); do [ -s "$W/id" ] && break; sleep 0.1; done
ID=$(head -1 "$W/id")
T1=$(date +%s%3N)
$R2PIPE recv "$ID" -o "$W/out.bin" --parallel "$PAR" --quiet --stats "$W/recv.json" 2>"$W/recv.err"
wait $SP
T2=$(date +%s%3N)
A=$(sha256sum "$IN" | cut -d' ' -f1); B=$(sha256sum "$W/out.bin" | cut -d' ' -f1)
[ "$A" = "$B" ] && OK=true || OK=false
node -e '
const [m,p,t0,t1,t2,ok,s,r]=process.argv.slice(1);const S=require(s),R=require(r);
const bytes=S.bytes;
console.log(JSON.stringify({mode:m,parallel:+p,bytes,wallSeconds:(t2-t0)/1000,pipelineMBps:+(bytes/1e6/((t2-t0)/1000)).toFixed(1),sendMBps:+S.mbps.toFixed(1),recvMBps:+R.mbps.toFixed(1),
 recvFirstByteMs:R.firstByteMs,recvFirstByteAfterSenderStartMs:R.firstByteUnixMs-S.startUnixMs,retries:S.retries+R.retries,verified:ok==="true"}))' "$MODE" "$PAR" "$T0" "$T1" "$T2" "$OK" "$W/send.json" "$W/recv.json"
