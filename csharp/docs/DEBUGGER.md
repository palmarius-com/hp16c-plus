# Debugging the calculator's ROM from VS Code

VoyagerSim can be attached to by a debugger while it runs, the way you attach to a process on a Raspberry Pi: the
calculator you see on screen is the one you debug, nothing is copied. It speaks the Debug Adapter Protocol (DAP, an
open protocol), so VS Code, or any editor with a DAP client, shows registers, RAM, a disassembly and breakpoints
without special code for the Nut CPU. This is for people who write patches and want to see what the ROM does.

## Turn it on

The debugger is off. Only a command-line option starts it, no menu item does:

```
VoyagerSim --model 11c --debug-port 4711
```

It listens on `127.0.0.1` only (this computer, not the network), on the port you give. Started from a terminal,
VoyagerSim prints what it does there with a time stamp: every connection, request, answer and event; connect and
disconnect also show in the window's status line. A double click has no terminal, so only the status line shows it.

## Connect VS Code

The folder `tools/vscode-nut` in the source is a very small VS Code extension: one `package.json` that declares the
debugger type `nut`. Load it without installing anything (PowerShell, absolute paths):

```
code --extensionDevelopmentPath="C:\path\to\vscode-nut" "C:\path\to\vscode-nut\test-workspace"
```

Then Run and Debug (Ctrl+Shift+D), pick "Nut: attach to the adapter" and press the green button. Attaching changes
nothing: the calculator keeps running and you keep using its window.

## What you can do

- **Pause** halts the CPU where it is. The window shows the frozen display, the status panel says HALTED, keys you
  press wait in a queue until you continue. **Continue** lets the CPU run again.
- **The ROM listing**: the ROM appears as a file `11C ROM.nutasm`, one line per address (line = address + 1), with
  Nonpareil mnemonics. A click in the left margin sets a breakpoint (VS Code needs the setting
  `debug.allowBreakpointsEverywhere` for this file type; the sample workspace sets it).
- **Step Into** runs one instruction (a two-word one counts as one); **Step Over** runs a `gosub` to its return;
  **Step Out** runs until the current routine returns.
- **Variables**: Registers (A, B, C, M, N, G, P, Q, FO), State (PC, carry, HEX/DEC, the S flags, cycles, the display
  text) and RAM (every RAM register, 14 hex digits).
- **Memory**: the icon next to a RAM entry (View Binary Data) opens the Hex Editor extension on the bytes. RAM
  `0x00000000`: register r at r x 8, bytes 0 to 6 are its 14 digits, most significant first, byte 7 is 0 (0xFF when the
  register does not exist). ROM from `0x00010000`, word w at 0x10000 + 2w, two bytes big endian, 10 bits used.
- **Debug Console**: `keys 2 ENTER 3 +` taps keys as the window does, `display` prints the LCD text, `reset` is
  batteries out and in, `help` lists them.
- **Disconnect** removes all breakpoints and lets the calculator run free.

## The instruction trace (--trace)

A log of what the CPU ran, off unless the command line asks for it. One line per instruction (a two-word instruction
is one line, the same unit Step Into uses), as plain CSV with a header line:

```
cycle,pc,words,mnemonic,repeat
1,0039,371 00C,?nc gosub 03DC,1
3,03DC,260,set hex,1
1500258,03E4,3C8,clr kb,6655
1500259,03E5,3CC,? kb,6655
1500260,03E6,3F7,?c  goto 03E4,6655
```

The `repeat` column: a group of up to 8 instructions that runs again and again in a row (here the key-wait loop) is
written once, every line of it with the number of times the group ran. Ordinary lines have 1. The first `cycle` of a
group is where it started, the next ordinary line's cycle tells you how long the loop took. The file holds no notes
and no comments, only the header and data lines.

| Option | Does |
|---|---|
| `--trace N` | keep the last N instructions in memory (up to 8,000,000); read them with the Debug Console command `trace 50` (the last 50) or `trace save FILE`, or the command API |
| `--trace-file FILE` | also write the instructions to a CSV file as they run, up to `--trace-max` lines (default 1,000,000) of output; at the limit no more lines are added and the status line says so |
| `--trace-regs` | add the registers as more CSV columns after `repeat`: `carry,A,B,C,M,N,P,Q,S` (14 hex digits each, S = the 14 status bits); lines only count as repeated when the registers are the same too |

The cycle column counts the CPU's instruction cycles since the calculator started, so two lines tell you what an
instruction cost. When you choose another calculator in the Calculator menu, the file goes on. A loop that is still
running is closed in the file when you ask for `trace` or `trace save`, and when the app ends. With none of the
options there is no cost.

## Good to know

- **A sleeping calculator.** Most of the time the CPU sleeps at PC 0000 waiting for a key. Step does nothing then, and
  the stop text says so. A breakpoint on line 1 (address 0000) stops on every key press; step from there.
- **After ON the display shows `Pr Error`**, as on a real Voyager. The first key clears it.
- **Keys go to the calculator you debug**: click them in the window, or use the Debug Console.
- **Breakpoints are checked before each instruction**, but only while a debugger is attached and has set some; with
  none, the CPU runs as fast as before.
- **No HP firmware is shipped.** The listing comes from the ROM you loaded, at run time.

## Not there yet

Symbols and comments in the listing, `f`/`g` key names in the console, a watch on RAM writes, and the Registers,
Memory and Disassembly panels some other extensions add to the sidebar (they need their own adapter and stay empty).
