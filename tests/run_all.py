"""Run every 16C test with a moderate case count and summarize.

    python tests/run_all.py [--scale 1.0] [--seed 1]
"""
import argparse
import pathlib
import subprocess
import sys
import time

HERE = pathlib.Path(__file__).resolve().parent
TESTS = [  # (script, cases at scale 1)
    ("test_stock16c.py", None),
    ("test_asm_roundtrip.py", None),
    ("test_model_vs_rom.py", 40),
    ("test_display_vs_rom.py", 600),
    ("test_cond_vs_rom.py", 60),
    ("test_regs_vs_rom.py", 200),
    ("test_indirect_vs_rom.py", 150),
    ("test_float_vs_rom.py", 150),
    ("test_float2int_vs_rom.py", 150),
    ("test_mem_flags_vs_rom.py", 40),
    ("test_dsz_isz_vs_rom.py", 80),
    ("test_plus_entry.py --stock --bsp", 40),   # entry rules on the stock ROM
    # 16C+ (needs tools/build_plus.py, run first below)
    ("test_plus_stage1.py", None),
    ("test_plus_display.py", 60),
    ("test_plus_entry.py --bsp", 60),
    ("test_plus_funcs.py", 40),
    ("test_plus_regs.py", 30),
    ("test_plus_float.py", 150),
    ("replay_cases.py --plus ../build/16cplus.obj --shuffle", None),
]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--scale", type=float, default=1.0)
    ap.add_argument("--seed", type=int, default=1)
    args = ap.parse_args()
    failed = 0
    t0 = time.time()
    b = subprocess.run([sys.executable, str(HERE.parent / "tools" / "build_plus.py")],
                       capture_output=True, text=True)
    print(f"{'ok  ' if b.returncode == 0 else 'FAIL'} build_plus.py "
          f"{b.stdout.strip().splitlines()[-1] if b.stdout.strip() else b.stderr[-300:]}")
    failed += b.returncode != 0
    for script, n in TESTS:
        parts = script.split()
        script = parts[0]
        extra = [str((HERE / a).resolve()) if a.endswith(".obj") else a
                 for a in parts[1:]]
        cmd = [sys.executable, str(HERE / script)] + extra
        if n:
            cmd += ["-n", str(max(1, int(n * args.scale))), "--seed", str(args.seed)]
        r = subprocess.run(cmd, capture_output=True, text=True)
        last = (r.stdout.strip().splitlines() or ["(no output)"])[-1]
        status = "ok  " if r.returncode == 0 else "FAIL"
        failed += r.returncode != 0
        print(f"{status} {script:28} {last}")
        if r.returncode:
            print(r.stdout[-2000:], r.stderr[-2000:])
    print(f"{'all passed' if not failed else f'{failed} FAILED'}, "
          f"{time.time() - t0:.0f} s")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
