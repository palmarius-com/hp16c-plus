# HP-16C+

The HP-16C with word sizes up to 128 bits, as a patch in Nut machine
code. It runs on the HP-15C hardware model: the HP-16C ROM on the 15C
chip map, with the new code in the 15C's second ROM chip. The idea comes
from J-F Garnier's "Challenge for AIs" on the Museum of HP Calculators
forum ([thread 25536](https://www.hpmuseum.org/forum/thread-25536.html)).

The repository also contains the tools it was built and tested with: a
Nut CPU / Voyager emulator in Python, written from scratch, an
assembler and disassembler, a reference model of the HP-16C integer
functions, and the tests.

**No HP ROMs are included.** The tools download the 16C and 15C ROM
dumps from Eric Smith's Nonpareil repository once
(`tools/fetch_nonpareil.py`) and keep them out of git.

## Status

Every integer function works at word sizes 65-128: entry and display
in all bases, arithmetic including DBL x, DBL / and DBLR, logic, shifts
and rotates, bit functions, registers and I, indirect addressing,
conditionals, DSZ/ISZ, SQRT, FLOAT, MEM, and the self-test. At word
sizes up to 64 the original code runs unchanged.

Tested in the Python emulator and the C# simulator in this repository.
Amphitryon (MoHPC forum) ran the 76 sequences of the test sheet on his NutEm/PC (a
second, independent interpreter of the firmware); every display
matched. He also reported two bugs (FLOAT n above 64 bits, BSP in float
mode), fixed in 0.5.0.

**On a real HP-15C Collector's Edition:** the 16C+ runs there, 128-bit
values included (tested on one calculator, 2026-11-07). The CE firmware
runs the Nut code in an ARM emulation with two differences from
Nonpareil (`c=stk`, `goto c`); the patch avoids both. `csharp/15c-ce/`
holds the upload kit (PowerShell, no HP code in it: the image is built
from your own flash dump) with `Upload_to_15c_CE.md` and the undo; since 0.5.5 the
simulator does the same itself, in under a minute (see below).
Use it at your own risk.

## How it works

- `WSIZE` 65-128 switches "plus mode" on; 1-64 is the original 16C.
- The new code lives at 0x2000-0x37FF (the 15C's second ROM chip, about
  5700 of 6144 words used). The 16C ROM gets 31 changed words
  (hooks, a checksum word and the nine words of the CHS stack-lift fix), so each chip still sums to 1 mod 1023
  and the self-test passes in the simulator. The patch avoids `c=stk`
  and `goto c` (they differ on the 15C CE).
- The 16C+ includes the CHS stack-lift fix of the 16C (in float mode, after `CLx` or `BSP` with X = 0, `CHS 7` overwrote
  X instead of lifting the stack): bug found and first patches by Thomas Klemm (MoHPC forum, thread 5321), the 16C
  version checked and passed on by Amphitryon. The test sheet has the cases at word sizes 16, 65, 100 and 128.
- The upper bits of X, Y, Z, T, LSTx and I live in RAM 0x10-0x17 (the
  15C's extra RAM). Storage registers hold ceil(W/4) nibbles in the
  original register pool layout.
- Behaviour above 64 bits follows the 16C handbook rules extended to
  the larger word size; `model/hp16c_model.py` defines it.

## Layout

| Path | Content |
|---|---|
| `nut/cpu.py` | Nut CPU emulator |
| `nut/voyager.py` | Voyager calculator (keyboard, LCD) around the CPU |
| `nut/hp16c.py` | HP-16C / 16C+ machine for tests: keys, state, display |
| `nut/asm.py`, `nut/disasm.py` | Nut assembler and disassembler |
| `nut/routine.py` | Call single Nut routines (unit tests) |
| `model/hp16c_model.py` | Reference model of the 16C integer functions |
| `plus/src/16cplus.asm` | The 16C+ source |
| `re/16c.sym` | Labels and comments for the 16C ROM (no ROM code) |
| `tools/build_plus.py` | Assemble the 16C+ into `build/16cplus.obj` |
| `tools/make_patch.py` | Assemble any Nut source into a patch file |
| `csharp/` | C# emulator and the desktop simulator |
| `tools/fetch_nonpareil.py` | Download the ROMs and chip files |
| `tools/make_cases16c.py` | Generate the stored test cases |
| `tools/depth16c.py` | Measured return stack depth of the new code |
| `tools/*16c.py` | ROM analysis: call tree, traces, function table |
| `tests/` | Tests; `tests/cases/` stored test cases |

## Getting started

Python 3.10 or newer, standard library only.

```sh
python tools/fetch_nonpareil.py      # once: ROMs and chip files
python tools/build_plus.py           # build/16cplus.obj
python tests/run_all.py              # every test, a few minutes
python tests/run_all.py --scale 0.2  # quicker
```

A 128-bit multiply in the emulator (word size 80 hex = 128):

```python
from nut.hp16c import HP16C

c = HP16C(plus=["build/16cplus.obj"]).start()
print(c.run("HEX 8 0 WSIZE F F F F F F F F F F F F F F F F ENTER 3 *"))
print(c.run("WINDOW 2"))    # ' FFFFFFFd .h', then '        2 h.'
```

`HP16C()` without `plus` is the stock 16C.

## Desktop simulator (C#, Avalonia)

The Palmarius Voyager simulator, version 0.5.5. It can also put one of your 16C-based
calculators (the 16C+, your own patch) on a HP-15C Collector's Edition: Calculator > Update a HP-15C CE...
(dump, identify the calculator by its serial number, upload, restore; it reads everything back, saves what it
overwrites, and a dump brings the calculator back; see the user guide, section 9). While that screen is open the
calculator simulation is stopped and a VS Code debugger is disconnected. The faceplates carry the
Palmarius mark (three bars and the blue square) where the maker's logo
would be.

`csharp/` holds a C# port of the emulator (`Nut.Core`, checked against
the same stored test cases) and a desktop app (.NET 10). It runs the
**HP-10C, 11C, 12C, 15C and 16C** and the 16C+, with a faceplate for each
model, and your own calculators (a ROM plus patches, saved under a name).

A ready-to-run Windows build (needs the .NET 10 Runtime) is on the
[releases page](https://github.com/palmarius-com/hp16c-plus/releases/latest):
`VoyagerSim.exe`, an `examples` folder and a `15c-ce` folder (the upload kit and the SAM-BA driver for Windows). The source builds on macOS and
Linux too, but only Windows is released and tested.

```sh
dotnet run --project csharp/src/Hp16c.App                 # the simulator
dotnet run --project csharp/src/Hp16c.App -- --model 15c  # start a model directly
dotnet run --project csharp/tests/Nut.Replay -c Release   # 1559 cases, about 1 s
dotnet run --project csharp/tests/App.Flow -c Release -- --sheet FILE --patch FILE
```

`App.Flow` runs the app's ROM store, session and key queue without the
window: download, a test sheet of key sequences, patch, sheet again.

- **No HP firmware included.** The start page gets each ROM with one
  click from Eric Smith's Nonpareil repository, or from a file you
  have. The app checks that it is an original ROM (by fingerprint) and
  keeps a copy in the `data/roms` folder next to the exe.
- **Real speed:** the Nut CPU at its own pace (215 kHz, 56 clocks per
  word, about 3,800 instruction words a second) or unleashed. Pauses
  (PSE) and long calculations take the real time at Real speed.
- **Patches:** ROM > Apply a patch puts your words on top of the
  original ROM; ROM > Export full ROM writes the patched ROM (Nonpareil
  `.rom` or NutEm/PC `.MClst`), ROM > Load full ROM runs one. Code
  beyond a model's own ROM runs on the 15C's larger chip layout
  automatically; ROM > ROM information shows the chip checksums.
- **Skins:** generated from each model's chip file (the 16C has a
  hand-made one), or your own as a JSON file (Skin > Save current skin
  as a template, edit, Skin > Load skin file).
- **Status panel:** Nut CPU registers, flags, return stack, the 16C
  stack, the last program counters and the cycle count (instruction
  cycles since reset; the key wait loop is left out and counted
  separately).
- **Resets:** Calculator > Battery replacement clears the memory and
  starts the ROM cold (Pr Error); the chord ON + - is the calculator's
  own reset.
- **Debugger:** started with `--debug-port N`; VS Code attaches to the
  running simulator over the Debug Adapter Protocol (a small extension in
  `csharp/tools/vscode-nut`): pause, breakpoints on ROM addresses, step
  into / over / out, registers, RAM in the Hex Editor and a Debug Console
  (`keys`, `display`, `trace`). Tested on Windows with VS Code only. See
  [csharp/docs/DEBUGGER.md](csharp/docs/DEBUGGER.md).
- **Trace and key log:** `--trace N` keeps the last N executed
  instructions, `--trace-file` writes them as CSV (cycle, address, words,
  mnemonic, loop count, optionally the registers). `--record FILE` writes
  every key you press with the display after it, as test lines.
- **Tests:** the Test window runs test files (key sequences with the
  expected display, plus `wait`, `speed`, `reset`, `chord { ... }`).
  `csharp/docs/examples/` has the examples of the Owner's Handbook of the
  10C, 11C, 12C, 15C and 16C as test files, with the handbook programs
  keyed in from the listings. They run clean on their own calculator
  (10C 536, 11C 1556, 12C 1275, 15C 770 and 16C 447 checks); the 15C file
  has 165 lines switched off (`##`, the reason below each).
- **Guides** (also under Help in the app):
  [user guide](csharp/docs/USER-GUIDE.md),
  [how to write a patch](csharp/docs/PATCHING.md),
  [how to write a test](csharp/docs/TESTS.md),
  [debugger](csharp/docs/DEBUGGER.md),
  [automation interface](csharp/docs/API.md).
  `tools/make_patch.py` assembles a Nut source file into a patch and can
  fix the chip checksums.
- The 16C+ needs its patch: `python tools/build_plus.py` writes
  `build/16cplus.obj`. Start the 16C, ROM > Apply a patch, then Calculator >
  Save as my calculator: it is your first own calculator, with its own
  Start button under "Your calculators".
- `csharp/src/Nut.Core/ChipDefs/` ships Nonpareil's chip definitions
  (keyboard and LCD maps, GPLv3, no firmware).
- `csharp/nuget.config` maps the Avalonia packages to nuget.org, for
  machines with NuGet package source mapping switched on.

## Tests

The reference model was checked against the original ROM on about
20000 random cases at word sizes 1-64; above 64 bits it decides what is
right. The 16C+ tests compare plus mode with the model; the stock tests
make sure the original behaviour at W <= 64 is unchanged (the stored
cases are replayed on the 16C+ as well).

| Test | Checks |
|---|---|
| `test_stock16c.py`, `test_asm_roundtrip.py` | emulator, assembler |
| `test_*_vs_rom.py` | model vs stock ROM |
| `test_plus_*.py` | 16C+ vs model above 64 bits |
| `replay_cases.py` | stored cases on stock 16C or 16C+ |

### Stored test cases

`tests/cases/*.jsonl`, generated by `tools/make_cases16c.py` (seed
2026): one JSON object per line, computed by the model and checked on
the stock ROM. Each case sets its own state (clears an error, sets flag
3, base, word size, complement mode, flags 4/5, I and the stack), so
cases work in any order and on other emulators or a real calculator.

| Field | Meaning |
|---|---|
| `id`, `area`, `seed` | name, file, generator seed |
| `op`, `w`, `comp`, `base` | what is tested (where it applies) |
| `keys` | key names; the full sequence |
| `keycodes` | the same as HP row/column key codes, prefixes included |
| `expect.display` | LCD text after the last key, 11 positions |
| `expect.x .. lastx` | registers as hex, masked to the word size |
| `expect.i` | I as 17 hex digits (68 bits) |
| `expect.c`, `expect.g` | flags 4 and 5 (annunciators C and G) |
| `expect.error` | `Error n` instead of a result |
| `expect.result`, `skipped` | conditionals, DSZ/ISZ: result, skip |
| `expect.m`, `display_float` | float: BCD image of X, float display |

Key names are the labels on the 16C keyboard, shifted functions by
their own name (the f or g prefix is added): `WSIZE` = f STO, `DBLX` =
g x, `X<>I` = f GTO, `SF` = g 4, `1S`/`2S`/`UNSGN` = f 1/2/3. The full
table is `KEYS` in `nut/hp16c.py`.

Display text: position 0 is the sign, positions 1-10 the digits. A
character is followed by `.` if its decimal point is lit, `,` for a
comma. Hex digits as the LCD shows them (`A b C d E F`).

### Known deviation

FLOAT n: the model rounds the exact value y * 2^x once. The ROM computes
2^|x| as a float and multiplies or divides with its 13-digit internal
arithmetic, which can end one unit off in the 10th digit on near-ties
(about 0.1% of random values). One stored float case was rejected for
this reason.

## Credits

- Eric Smith's [Nonpareil](https://github.com/brouhaha/nonpareil)
  (GPLv3): the emulator's instruction set and display behaviour follow
  its source (`proc_nut.c`, `digit_ops.c`, `voyager_lcd.c`), and the
  chip map, keyboard and LCD layout are read from its `.ncd` files.
- J-F Garnier for the challenge, and for what he knows about the real
  Voyagers (the ON + y^x gesture, the timing).
- Amphitryon (MoHPC forum), author of the NutEm/PC firmware
  interpreter: he pointed us to the challenge, added a "16C+" model to
  NutEm/PC so the patch can be checked on a second, independent
  interpreter, and pointed to the words 0x13FD-0x13FF in the 16C ROM
  (0x13FF is now the 16C+ checksum word in that chip). He has also
  tested the simulator since 0.3.0 and found many of the fixes in it.
- Written with AI help (Claude, Anthropic).

HP-16C and HP-15C are products of HP; their firmware is not part of
this repository.

## License

GNU General Public License v3.0, see [LICENSE](LICENSE).
