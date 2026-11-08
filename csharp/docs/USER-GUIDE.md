# Palmarius Voyager simulator: user guide

The simulator runs the real firmware of five HP calculators from the
1980s, the "Voyager" series: the HP-10C, 11C, 12C, 15C and 16C. Your own
calculators run next to them: original ROMs with patches, such as the
HP-16C+, a 16C extended to 128-bit integers.

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
in the `data/roms` folder next to the program (ROM > Open the ROM folder),
so the whole simulator stays in one folder: unzip it somewhere you can write
to, not under Program Files. You only do this once
per calculator. "ROM ready" in green means the calculator can start.

## 2. Using the calculator

- Click the keys with the mouse. Holding the mouse button holds the
  key, as on the real calculator (the 16C's STATUS shows while held).
- Or type on the PC keyboard: digits, Enter, + - * /, Backspace, the
  decimal point; on the 16C also A-F. F1 = f (gold), F2 = g (blue),
  F3 = ON.
- The calculator starts switched off. Press ON (or F3).
- After a fresh start the display shows `Pr Error` (as a real one does
  after new batteries). Any key clears it.
- Keys pressed together: switch the calculator off (ON), then right-click the key that goes first and choose.
  While the calculator is on, the menu only says that chords need it off; nothing is switched off for you.
  After a fresh start (or a battery replacement) it says the same until the calculator has been switched on once:
  the first ON clears the memory and shows Pr Error, and a chord pressed first is lost to that, as on the real one.
  ON: self-test, continuous self-test, keyboard test. `−`: memory reset,
  which clears everything. `·`: swap decimal point and comma. `yˣ` (on the 12C `PMT`, the same key): ON + yˣ
  for a frozen calculator, from the handbooks: the CPU starts over and a
  running or hung program is gone, the memory stays (10C, 11C, 12C, 15C; the 16C has
  no such chord). Calculator > CPU reset does the same, at any time. By hand a chord also works: calculator
  off, hold ON with the mouse, press the other key on the PC keyboard,
  release both.
- Calculator > Real speed runs the calculator as slowly as the real
  one (handy for programs that pause). Without it, the simulator runs
  as fast as your PC allows.
- Calculator > Battery replacement (memory lost): what taking the batteries out does. Every memory register is lost,
  the ROM starts cold and shows Pr Error. The chord ON + - (from a switched-off calculator) is the ROM's own reset and
  stays as it is.
- Calculator > CPU reset, memory kept (ON + yˣ; 12C: ON + PMT): the CPU starts over and runs to sleep, then ON wakes it; stack and
  registers stay as they were. X shows 0: what the real calculator shows afterwards is not known (it reportedly "usually
  leaves NNN in X"), so this part may differ.

## 3. The status panel

View > Status panel shows what happens inside: the registers of the
Nut processor (A, B, C, M, N, the pointers P and Q, the status bits),
the return stack, the last program addresses the processor ran and,
on the 16C, the X, Y, Z, T, LAST X and I registers. You do not need
it to use the calculator; it is there for the curious and for people
who write patches.

View > Repaint all draws the whole window again. It is only for the rare case that
something looks wrong: the calculator draws its LCD and a pressed key on their
own, and the rest only when the skin, label, size or calculator changes.

The line "cycles since reset" counts the Nut processor's instruction cycles, one per
instruction word it executes (not the host computer's cycles). Right after choosing a model it already
shows 33 or so: the power-on run until the first sleep. A real Voyager executes about 3,840 of them a
second (215 kHz, 56 clocks per word), so a count divided by 3,840 is the time on the real calculator.
The line "last key" does that for you: the cycles from a key going down until the calculator sleeps
again, and the same in milliseconds at real speed. While a key is down the processor goes round the ROM's key wait loop, which changes nothing; at full speed a key held
for a few milliseconds of your time makes millions of those. They are left out of both lines (the status line says how
many): the count is the work, and it agrees with the real calculator's word time (`-99.5 x!` on the 11C: about 69,000). A state file saves the count with the rest;
Calculator > Reset the cycle counter starts it, and "last key", from 0 again.

While a program runs, the calculator shows `running`, and after an overflow the display flashes. As on the
real calculator, the firmware only tells the display chip to blink; the chip then switches the display on and off by
itself (about 0.6 seconds each), whatever the processor does. So it keeps blinking at full speed and while the
processor sleeps.

### Saving and loading the state

At the bottom of the status panel, "Save state..." writes everything the
calculator holds at that moment (registers, memory, programs, the
display) into a `.json` file you can read in any text editor: registers
as hex digits, memory listed register by register. "Load state..." puts
it back, so you can return to exactly that point later, or send it to
someone else.

If you set a faceplate label (Skin > Faceplate label), the file keeps it, and the name
of the skin file in use. Loading puts the label back unless you have set another one
since (yours stays), and tells you if the skin was a different one; it does not load
a skin for you (Skin > Load skin file).

The file carries a checksum. Reading it is fine, but a state file that
was changed after saving is refused, as are files from before version
0.2.0, which have no checksum.

A state belongs to one calculator: the file records the calculator, the
ROM's fingerprint, the patches and the chip layout. Another calculator or
chip layout is refused with a message saying why. If only the ROM differs,
for example because you changed your patch, the simulator asks: load
anyway, or cancel.

## 4. Skins

The look of the keys is called a skin.

- Skin > Automatic: the legends come from the calculator's chip file,
  so every model gets its own faceplate, with the gold CLEAR, BOND and
  DEPRECIATION brackets as printed. The 16C has a hand-made one.
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
- Checksum: if a patch or a ROM file you load leaves a chip that does not add
  up to 1 (the self-test's rule), the app asks "Update the checksum?" and says
  which word it would change (a word at the end of a 1K page of that chip that is unused: 000 in
  the original ROM, or one a patch set as its checksum word). Yes adds the change
  as a patch called "checksum update"; No leaves everything as it is, and the
  calculator runs either way.
- A ROM file is lines of `aaaa:www`; a note after the word is fine
  (`01DA:3D0 del`), and `#` starts a comment.
- ROM > ROM information: what is loaded, and whether the ROM
  checksums are right (the calculator's self-test checks them). The checksum
  is checked per chip; a ROM with code in 1800-1FFF is checked with a second
  chip of 8K at 1800-37FF, as such ROMs are made.
- ROM > Export full ROM: writes the complete, patched ROM as one
  file, as Nonpareil text (`.rom`) or for NutEm/PC (`.MClst`). It contains
  HP's firmware, so the simulator reminds you to keep it for yourself.
- ROM > Export patch: writes only the words that differ from the original
  ROM. That file is yours to share; anyone can apply it to their own
  original ROM.
- ROM > Load full ROM file: runs such a file directly. If it is not an
  original ROM, the simulator asks which calculator should run it.

If a patch has code beyond the calculator's own ROM, the simulator
presets the larger chip layout of the 15C and says so. ROM > Chip layout
lets you choose: with the calculator's own chips, the words beyond them
are left out and the status line tells you what will not run. Changing
the layout restarts the calculator.

Exported ROMs contain HP's firmware: keep them for your own use, do
not publish them. Share patches instead: a patch holds only your own
words.

Help > How to write a patch explains how to make one.

### Your own calculators

When a patched or modified ROM works the way you want, keep it as a
calculator of its own:

1. Start the calculator and apply your patches (or load a full ROM).
2. Calculator > Save as my calculator...
3. Give it a name (for example `HP-16C+`), a short name for its folder
   (`HP16C+`), a label for the faceplate (`16C+`) and, if you like, a
   description. The button says what will happen: Save for a new short
   name, Update for the calculator you are saving again, Override (with a
   warning) if the name belongs to another of your calculators.

It then appears on the start page under "Your calculators", with its
own Start button, and starts with the right keyboard, display, label
and chip layout. No patch files are needed any more. From the command
line or a shortcut: `VoyagerSim --model HP16C+` (the short name, or the
full name). "Delete" on the
start page removes it again; the original ROMs stay.

Patches, the chip layout, the label and a loaded skin stay until you
switch to another calculator or close the app. If you changed something,
the simulator then lists what and asks whether to keep it as your own
calculator (Save, Don't save, Cancel). If nothing changed, it just
switches. A saved calculator keeps all of these.

What is stored: a folder per calculator, `data/custom/SHORTNAME`, with
a full ROM file, a small description file and the skin, if it has one.
ROM > Open the ROM folder gets you there. The ROM
file contains HP's firmware, so it is for you only: share the patch,
not the saved calculator.

### Your own tests

Help > Test window runs tests you write yourself, one line per test, on a
copy of the current calculator (same ROM and patches, its own memory):

```
1 2 [ENTER] 3 0 [+]  =>  '42 h'
```

Every key is in brackets, as the handbooks draw them; digits stay bare. Right
of `=>`, in ticks, is what the display must show. The window follows the run
on both sides and says `Finished:` with the counts at the end. A line that
fails shows what you expected and what the display had, both in ticks.

Ready-made files are in the `examples` folder of the download; load them with
Open...:

- `hp10c-handbook-tests.txt`: 536 tests from the HP-10C Owner's Handbook, for
  the HP-10C.
- `hp11c-handbook-tests.txt`, `hp12c-handbook-tests.txt`,
  `hp15c-handbook-tests.txt` and `hp16c-handbook-tests.txt`: the same for the
  other four. Each runs on its own calculator. Lines starting with `##` are
  switched off because their check fails in the simulator; the reason is
  below each.
- `16cplus-test-sheet.txt`: 77 tests for the 16C+.

Help > How to write a test explains every line you can write.

## 6. The HP-16C+

The 16C+ is the 16C with word sizes up to 128 bits. It needs the 16C
ROM plus the 16C+ patch. It is not on the list of original calculators:
you make it once, and it becomes your first own calculator:

1. Start the HP-16C.
2. ROM > Apply a patch..., choose `16cplus.patch`. The simulator presets
   the 15C chip layout (the 16C+ code lives in the 15C's second ROM chip)
   and suggests the label 16C+.
3. Calculator > Save as my calculator..., name it `HP-16C+`.

From now on it is on the start page under "Your calculators", with its
own Start button. You need the patch file only this once.

The app recognises known builds by their fingerprint, so it suggests the
label 16C+ whatever the patch file is called, but only on the large chip
layout, where the 16C+ can really run. The label is free text: Skin >
Faceplate label sets your own; empty goes back to the automatic one.

Quick start: `HEX 8 0 f WSIZE` sets 128 bits; `f WINDOW 0` to `3` and
`g <` / `g >` show all 32 hex digits.

## 7. Command line options

Start `VoyagerSim` from a terminal or a shortcut with these options. They are all optional; without any, the app opens on
the start page.

| Option | Does |
|---|---|
| `--model NAME` | start that calculator at once: `10c`, `11c`, `12c`, `15c`, `16c`, or the short or full name of one of your own |
| `--api [PIPE]` | the command API for scripts and tests ([API.md](API.md)) |
| `--debug-port N` | lets VS Code attach to the running simulator and debug the ROM, on 127.0.0.1 port N ([DEBUGGER.md](DEBUGGER.md)) |
| `--trace N` | keep the last N executed instructions in memory (docs/DEBUGGER.md, "The instruction trace") |
| `--trace-file FILE` | also write the instructions to a CSV file as they run, up to `--trace-max` lines (default 1,000,000) |
| `--trace-regs` | add the registers as more CSV columns |
| `--record FILE` | write every key you press, with the display after it, as test lines (`[LBL]  =>  '001-42,21,11'`): the way to make a test file by hand, or to send someone a log of what you did |

The debugger and the trace cost nothing when they are off, and only the command line turns them on: there is no menu
item for them.

## 8. Which calculators work

Calculators with HP's Nut processor and the Voyager display: 10C,
11C, 12C, 15C, 16C. Other Nonpareil models (HP-25, 41C, ...) use other
processors or display chips and are not supported.

## 9. Putting your calculator on a HP-15C Collector's Edition

Calculator > Update a HP-15C CE... is a screen of its own: the simulation is stopped while it is open and nothing else
can touch the port. It works with the HP programming cable (USB; POGO connector in the battery compartment) on a
HP-15C Collector's Edition only, and at your own risk. HP's cable guide says the connection erases the user area
(programs, data, matrices): save what you need first.

1. Programming mode: cable on, ON, hold ERASE on the cable, tap RESET, release ERASE. Windows shows a port "Bossa
   Program Port (COMx)"; if not, install the driver once from the folder `15c-ce\driver` of the zip (right-click
   `atm6124_cdc.inf`, Install).
2. **Dump** reads the whole flash twice (about a minute) and saves it in `data\ce`. It is your backup: keep it. The
   file holds the calculator's firmware, so do not share it. **Save this dump as my calculator...** makes the ROM that
   the calculator runs in its 16 mode a calculator of yours (a 16C+ already installed, or the factory 16C ROM).
3. **Upload** puts one of your calculators in the 16 and the 15.2 mode of the CE (the factory 15 mode is never
   touched). Only calculators based on the HP-16C are offered: the 16C with a patch, the 16C+, your own modification.
   What is written is the words where your calculator differs from the original 16C, laid over the calculator's OWN 16C
   ROM (the CE's differs from Nonpareil's at six words). Tick the box that you understand it, press Upload; it takes
   under a minute, reads everything back, saves every page it overwrites (`data\ce\preimages-...`) and puts back
   what it wrote if anything fails. **Save what was written as a patch** keeps the differences from the backup as a patch file.
4. **Restore** writes the slot and the pointer back as a saved dump has them, then compares the whole flash with it.
   It is offered only when a dump of a known calculator exists.
5. Leave programming mode: RESET on the cable, then ON. Modes: `g` + `ENTER` + `ON`, then `4`, then `7` (the 16 mode)
   or `y^x` (15.2).

While a job runs the window cannot be closed, the menus are off and Windows is asked to wait with a shutdown. Nothing
can stop Task Manager, a power cut or a pulled cable, but the boot loader is never written (the calculator can always be
put into programming mode again) and Restore with your dump brings back what was there. A log of every byte that crossed
the port is in `data\ce\ce-programmer-*.log`.

The 16C+ on a real calculator: tried on one HP-15C CE; see the release notes for what is known and what is not.
