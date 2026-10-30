# Palmarius Voyager simulator: user guide

The simulator runs the real firmware of five HP calculators from the
1980s, the "Voyager" series: the HP-10C, 11C, 12C, 15C and 16C. It also
runs the HP-16C+, a 16C extended to 128-bit integers.

## 1. First start: get the ROM

The firmware (the "ROM") belongs to HP and does not come with the
simulator. You get it in one of two ways:

- **One click:** on the start page, press "Get ROM from Nonpareil" next
  to the calculator you want. The simulator downloads the ROM directly
  from Eric Smith's Nonpareil project on GitHub:
  https://github.com/brouhaha/nonpareil (folder `ncd/`). The exact
  address is `https://raw.githubusercontent.com/brouhaha/nonpareil/HEAD/ncd/15c/15c.rom`
  for the 15C, and the same pattern with `10c`, `11c`, `12c`, `16c`.
  Nothing else is downloaded, and only when you press the button.
- **From a file:** if you already have a Nonpareil ROM file (for example
  `15c.rom`), press "Open an original ROM file...".

The simulator checks that the file is an original ROM and keeps a copy
in its ROM folder (ROM > Open the ROM folder). You only do this once
per calculator. "ROM ready" in green means the calculator can start.

## 2. Using the calculator

- Click the keys with the mouse. Holding the mouse button holds the
  key, as on the real calculator (the 16C's STATUS shows while held).
- Or type on the PC keyboard: digits, Enter, + - * /, Backspace, the
  decimal point; on the 16C also A-F. F1 = f (gold), F2 = g (blue),
  Esc = ON.
- The calculator starts switched off. Press ON (or Esc).
- After a fresh start the display shows `Pr Error` (as a real one does
  after new batteries). Any key clears it.
- Keys pressed together: a few functions use ON plus a second key, always
  starting with the calculator off. Hold ON with the mouse, press the
  second key on the PC keyboard, release both. ON + `*` is the self-test,
  ON + `-` the memory reset.
- Calculator > Real speed runs the calculator as slowly as the real
  one (handy for programs that pause). Without it, the simulator runs
  as fast as your PC allows.
- Calculator > Batteries out and in: a full reset.

## 3. The status panel

View > Status panel shows what happens inside: the registers of the
Nut processor (A, B, C, M, N, the pointers P and Q, the status bits),
the return stack, the last program addresses the processor ran and,
on the 16C, the X, Y, Z, T, LAST X and I registers. You do not need
it to use the calculator; it is there for the curious and for people
who write patches.

### Saving and loading the state

At the bottom of the status panel, "Save state..." writes everything the
calculator holds at that moment (registers, memory, programs, the
display) into a `.json` file you can read in any text editor: registers
as hex digits, memory listed register by register. "Load state..." puts
it back, so you can return to exactly that point later, or send it to
someone else.

A state belongs to one calculator with one ROM: the file records the
calculator, the ROM's fingerprint and the patches. Loading it into a
different combination is refused with a message saying what to start
first.

## 4. Skins

The look of the keys is called a skin.

- Skin > Automatic: the legends come from the calculator's chip file,
  so every model gets its own faceplate. The 16C has a hand-made one.
- Skin > Save current skin as a template: writes a `.json` file. Open
  it in any text editor, change the legends or the colours, save it,
  then Skin > Load skin file. Colours are written like `#E0A83C`.

Each key in the file has its keycode (row and column, e.g. `36` is
row 3, column 6), the main legend, the gold `F` legend above and the
blue `G` legend below. `Rows: 2` makes a key two rows high (ENTER).

## 5. Patches and modified ROMs

A patch changes some words of the ROM. With patches people fix,
extend or experiment with the original firmware; the 16C+ is one.

- ROM > Apply a patch...: choose a patch file. It goes on top of the
  ROM that is running. You can apply several.
- ROM > Remove all patches: back to the original.
- ROM > ROM information: what is loaded, and whether the ROM
  checksums are right (the calculator's self-test checks them).
- ROM > Export full ROM: writes the complete, patched ROM as one
  file, as Nonpareil text (`.rom`) or for NutEm/PC (`.MClst`).
- ROM > Load full ROM file: runs such a file directly. If it is not an
  original ROM, the simulator asks which calculator should run it.

If a patch has code beyond the calculator's own ROM, the simulator
uses the larger chip layout of the 15C automatically (and says so).

Exported ROMs contain HP's firmware: keep them for your own use, do
not publish them. Share patches instead: a patch holds only your own
words.

Help > How to write a patch explains how to make one.

### Your own calculators

When a patched or modified ROM works the way you want, keep it as a
calculator of its own:

1. Start the calculator and apply your patches (or load a full ROM).
2. ROM > Save as my calculator...
3. Give it a name (for example `HP-16C+`), a short label for the
   faceplate (`16C+`) and, if you like, a description.

It then appears on the start page under "Your calculators", with its
own Start button, and starts with the right keyboard, display, label
and chip layout. No patch files are needed any more. From the command
line or a shortcut: `VoyagerSim --model HP-16C+` (the name, or the
short id the app shows after saving, e.g. `hp-16cp`). "Delete" on the
start page removes it again; the original ROMs stay.

What is stored: a full ROM file and a small description file, both in
the ROM folder (ROM > Open the ROM folder, subfolder `my`). The ROM
file contains HP's firmware, so it is for you only: share the patch,
not the saved calculator.

## 6. The HP-16C+

The 16C+ is the 16C with word sizes up to 128 bits. It needs the 16C
ROM plus the 16C+ patch:

1. Start the HP-16C.
2. ROM > Apply a patch..., choose `16cplus.patch`. The simulator
   switches to the 15C chip layout by itself (the 16C+ code lives in the
   15C's second ROM chip) and the faceplate says 16C+.
3. ROM > Save as my calculator..., name it `HP-16C+`. From now on it
   has its own Start button.

Calculator > HP-16C+ does steps 1 and 2 in one go when the patch is
found in the project's `build/` folder.

Quick start: `HEX 8 0 f WSIZE` sets 128 bits; `f WINDOW 0` to `3` and
`g <` / `g >` show all 32 hex digits.

## 7. Which calculators work

Calculators with HP's Nut processor and the Voyager display: 10C,
11C, 12C, 15C, 16C. Other Nonpareil models (HP-25, 41C, ...) use other
processors or display chips and are not supported.
