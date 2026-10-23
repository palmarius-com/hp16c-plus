"""Fetch the Nonpareil HP-15C/16C chip definitions and ROM dumps.

Downloads from Eric Smith's Nonpareil repository into ../nonpareil/.
The .rom files are HP firmware and stay out of git (see .gitignore);
run this once per machine.

    python tools/fetch_nonpareil.py
"""
import pathlib
import urllib.request

BASE = "https://raw.githubusercontent.com/brouhaha/nonpareil/HEAD/ncd/"
FILES = {
    "15c/15c.ncd.tmpl": "15c.ncd.tmpl",
    "15c/15c-192.ncd.tmpl": "15c-192.ncd.tmpl",
    "15c/15c-192.patch": "15c-192.patch",
    "15c/SConscript": "15c.SConscript",
    "15c/15c.rom": "15c.rom",
    "16c/16c.ncd.tmpl": "16c.ncd.tmpl",
    "16c/16c.rom": "16c.rom",
}
# Nonpareil sources used as the spec for nut/ (GPLv3, reference only)
SRC = ["proc_nut.c", "proc_nut.h", "dis_nut.c", "voyager_lcd.c",
       "voyager_lcd.h", "digit_ops.c", "digit_ops.h", "proc_int.h"]
FILES.update({f"../src/{f}": f"src/{f}" for f in SRC})

out = pathlib.Path(__file__).resolve().parent.parent / "nonpareil"
(out / "src").mkdir(parents=True, exist_ok=True)
for src, dst in FILES.items():
    with urllib.request.urlopen(BASE + src) as r:
        (out / dst).write_bytes(r.read())
    print("fetched", dst)
