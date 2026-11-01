# Nut CPU debugger for VS Code

Remote debugging of the ROM in the running simulator: you start VoyagerSim, VS Code attaches to that
very process. Nothing is copied; the calculator you see is the one you debug.

- `csharp/src/Nut.Dap`: a Debug Adapter Protocol server (C#, part of VoyagerSim). It listens on
  `127.0.0.1` only, and only when the app is started with `--debug-port`; no menu item turns it on.
- this folder: the smallest possible VS Code extension. It only declares a debugger type `nut`, so
  VS Code knows what to do with `"type": "nut"` in a launch configuration. No code, just `package.json`.

## Try it

1. Start the simulator with the debug port (any model; the debugger follows the calculator that is
   running, also when you choose another one from the Calculator menu):

   ```
   VoyagerSim --model 11c --debug-port 4711
   ```

   From source: `dotnet run --project csharp/src/Hp16c.App -- --model 11c --debug-port 4711`

2. Open VS Code with this extension loaded, on the test workspace (this starts a second VS Code
   window, the "Extension Development Host"; nothing is installed). PowerShell, from the repository root:

   ```
   code --extensionDevelopmentPath="$PWD\HP16Cplus_challenge\csharp\tools\vscode-nut" "$PWD\HP16Cplus_challenge\csharp\tools\vscode-nut\test-workspace"
   ```

   Use absolute paths: the Extension Development Host resolves a relative `--extensionDevelopmentPath`
   against the drive root, finds nothing, and reports "Configured debug type 'nut' is not supported".

3. In that window: Run and Debug (Ctrl+Shift+D), pick "Nut: attach to the adapter", press F5.
   Attaching changes nothing: the calculator keeps running. Use the calculator window as usual.

## What to look at

- **Pause** (the toolbar button) halts the CPU thread where it is. The window stays alive and shows the
  frozen display; the status panel says HALTED; keys you press wait in the queue until you continue.
- **Variables**: Registers (A, B, C, M, N, G, P, Q, FO), State (PC, carry, HEX/DEC, S flags, cycles, the
  display text) and RAM (every RAM register that exists, 14 hex digits). Click the top frame first.
- **Call stack**: the first frame is the PC, then the four return-stack entries.
- **Memory**: RAM registers and the State scope's RAM and ROM entries carry a memory reference, so VS Code
  offers *View Binary Data* next to them (it uses the Hex Editor extension and may ask to install it).
  Flat byte map: RAM `0x00000000-0x00001FFF`, register r at r x 8, bytes 0 to 6 are its 14 hex digits most
  significant first, byte 7 is 0x00 (0xFF when the register does not exist); ROM from `0x00010000`, word w at
  0x10000 + 2w, big endian, 10 bits used (0xFF where there is no ROM).
- **The terminal tells what happens**: started from a terminal with `--debug-port`, VoyagerSim prints every
  connection, request, answer and event there with a time stamp, and connect/disconnect also show in the
  window's status line. (Double click: no terminal, status line only.)
- **ROM listing**: the ROM is offered as a virtual source file, `11C ROM.nutasm`: one line per address
  (line = address + 1), Nonpareil mnemonics, a second word of a two-word instruction on its own line marked
  `(operand of ...)`. When the CPU is halted the editor opens it at the current instruction. A click in the
  gutter sets a breakpoint on that address. (Why a source file: VS Code only inspects a frame, which fills
  Variables, when the frame has a source or after an instruction-level step.) The built-in disassembly
  view (*Debug: Open Disassembly View*) shows the same instructions from the PC on.
- **Gutter breakpoints need the setting** `debug.allowBreakpointsEverywhere` (the test workspace sets it). VS Code showed
  the listing as "Plain Text" instead of the declared `nutasm` language (cause not found), and plain text takes no
  breakpoints. A click in the left margin, between the arrow and the line number, toggles one (a red dot); a second
  click on the same line removes it again.
- **A sleeping CPU**: the calculator spends its time asleep, PC 0000, waiting for a key. Step does nothing
  then, and the stop text says so. The first instruction after a key wakes it is at 0000: a breakpoint on
  line 1 of the listing stops on every key press, then step from there.
- **Step Into / Step Over / Step Out / Continue**: Step Into is one instruction (a two-word one counts as one); Step Over runs a `gosub` to its return; Step Out runs until the current routine returns (calls on the way are run through).
- **Debug Console**: `keys 2 ENTER 3 +` taps keys as the window does, `display` prints the LCD text,
  `reset` is batteries out and in, `trace 50` prints the last 50 executed instructions and `trace save FILE` writes them (start VoyagerSim with `--trace N` for that, docs/DEBUGGER.md), `help` lists them.
- **Detach** (Disconnect) removes the breakpoints and lets the calculator run free.

## Not there yet

No symbols or comments in the listing, no `f`/`g` function names for the Debug Console keys (they are the first printed
legend of each key), no "watch RAM on write", one client at a time. A breakpoint is checked before every instruction,
but only while a debugger is attached and has breakpoints set. Tested on Windows with VS Code; macOS and Linux should
work (the protocol is open and the server is plain TCP) but have not been tried. The sidebar panels Peripherals,
Registers, Memory and Disassembly that some other extensions add stay empty for this debugger.
