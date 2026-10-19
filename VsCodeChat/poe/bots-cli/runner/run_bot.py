#!/usr/bin/env python3
"""Run a Poe script bot locally.  python run_bot.py bots/<handle>/bot.py --text "..." [--attach f] [--param k=v]"""
import argparse
import builtins
import json
import runpy
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import fp_shim  # noqa: E402
import poe_stub  # noqa: E402


def parse_param(s):
    k, _, v = s.partition("=")
    if not _:
        raise SystemExit("--param expects k=v, got %r" % s)
    try:
        return k, json.loads(v)
    except ValueError:
        return k, v


def run(script, text="", attachments=(), params=None, out_dir="out", bot_name=None, stdout=None):
    """Execute script; returns (messages, runtime, error_text_or_None)."""
    stdout = stdout or sys.stdout
    script = Path(script)
    fp_shim.install()
    rt = poe_stub.Runtime(text, attachments, params, out_dir, bot_name or script.parent.name)
    poe_stub._bind(rt)
    sys.modules["poe"] = poe_stub
    real_print = builtins.print

    def poe_print(*args, sep=" ", end="\n", file=None, flush=False):
        if file is not None:
            return real_print(*args, sep=sep, end=end, file=file, flush=flush)
        with poe_stub.start_message() as m:
            m.write(sep.join(str(a) for a in args) + ("" if end == "\n" else end))

    builtins.print = poe_print
    builtins.input = lambda *a: (_ for _ in ()).throw(NotImplementedError("input() is not supported on Poe"))
    err = None
    try:
        runpy.run_path(str(script), init_globals={"poe": poe_stub}, run_name="__main__")
        rt.finalize()
    except poe_stub.BotError as e:
        err = e.message
    finally:
        builtins.print = real_print
        rt.close()
    return rt.messages(), rt, err


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("script")
    ap.add_argument("--text", default="")
    ap.add_argument("--attach", action="append", default=[])
    ap.add_argument("--param", action="append", default=[])
    ap.add_argument("--out", default="out")
    a = ap.parse_args(argv)
    params = dict(parse_param(p) for p in a.param)
    msgs, rt, err = run(a.script, a.text, [Path(p).resolve() for p in a.attach], params, a.out)
    for m in msgs:
        sys.stdout.write(m.text.rstrip("\n") + "\n")
        for att in m.attachments:
            sys.stdout.write("[attachment saved] %s\n" % (Path(a.out) / att.name))
    if err is not None:
        sys.stderr.write("BotError: %s\n" % err)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
