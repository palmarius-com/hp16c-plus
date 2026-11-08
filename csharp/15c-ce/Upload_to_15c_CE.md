# Upload the HP-16C+ (word size up to 128 bits) to a HP-15C Collector's Edition

The HP-15C CE contains a hidden HP-16C mode. This kit replaces what runs in the **16** mode (and in
the **15.2** mode) with the **16C+**: the HP-16C with word sizes up to 128 bits, made by patching the
16C's Nut code (J-F Garnier's "Challenge for AIs", MoHPC forum thread 25536). The factory 15 mode is
not touched.

**The easy way (VoyagerSim 0.5.5 and later): `Calculator > Update a HP-15C CE...`.** The simulator has the programmer
built in: dump, upload one of your calculators, restore, with progress and checks on screen, in about a minute and a half
(the scripts of this folder need about 25 minutes). The driver of section 1 below is the same. The scripts stay in this
folder for those who prefer them, and as the reference of what the screen does.

**Read this first**

- You do this at your own risk. Nothing here comes from HP or Moravia, and the calculator is yours.
- It has been tried on **one** calculator. The kit refuses a calculator whose flash does not match
  that one (`known-units.txt`). If yours is refused, nothing has been written; tell us in the forum
  thread which regions the builder named.
- Only the **HP-15C Collector's Edition (CE)** with the HP programming cable (USB, POGO connector
  in the battery compartment). The cable must never be used on a 15C LE or other models, HP says it
  may destroy them.
- HP's own cable guide says that connecting the calculator erases the user area (programs, data,
  matrices). Back up what you need first.
- The kit contains **no HP code**. The image is built on your PC from the dump of your own
  calculator and stays there. Do not share your dump or the image: they contain HP's firmware.
- The boot loader (the part that lets you write at all) is never written; the tools refuse any write
  below flash address 0x4000. If a write stops half way, run `undo.ps1`.

## What you need

- Windows 10 or later, with Windows PowerShell 5.1 (it is part of Windows). No Python, no SAM-BA.
- The HP programming cable and the CE.
- This folder: `build_image.ps1`, `dump_flash.ps1`, `upload.ps1`, `undo.ps1`, `sam_ba_read.ps1`,
  `sam_ba_flash.ps1`, `16cplus.patch`, `known-units.txt`, `driver\`.
- Open PowerShell in this folder. If scripts are blocked, run every command as shown below
  (`-ExecutionPolicy Bypass`).

## 1. The driver (once)

The calculator in programming mode shows up as "Bossa Program Port" (USB 03EB:6124). Windows may not
install a driver for it by itself. `driver\atm6124_cdc.inf` and `.cat` are Microchip's driver package
for that port, copied unchanged from SAM-BA 3.9.2 (GPL v2, see `driver\NOTICE.txt`).
Install: right-click `atm6124_cdc.inf`, "Install", or in the Device Manager choose the port,
"Update driver", "Browse my computer", "Let me pick", "Have disk" and select the `.inf`.

## 2. Programming mode

HP's steps:

1. Open the battery compartment and plug the cable into the POGO connector (it fits one way only:
   push on both sides of the connector). Plug the USB side into the PC.
2. Turn the calculator on (`ON`).
3. Press and hold `ERASE` on the cable, press and release `RESET`, release `ERASE`.
4. In the Device Manager under Ports (COM & LPT) a new port "Bossa Program Port (COMx)" appears.
   Use that COM number as `-Port COMx` below (the examples say COM6).

## 3. Dump your calculator (the backup)

    powershell -ExecutionPolicy Bypass -File dump_flash.ps1 -Port COM6 -Out my-15c-ce-dump.bin

It reads the flash (128 KB) twice with read commands only and keeps the file only if both reads are
identical (about two minutes). **Keep `my-15c-ce-dump.bin`**: the next steps and the undo need it.

## 4. Build the image

    powershell -ExecutionPolicy Bypass -File build_image.ps1 -Dump my-15c-ce-dump.bin

It checks your dump against `known-units.txt` (refuses an unknown unit), takes your calculator's
own 16C ROM, puts the words of `16cplus.patch` over it and keeps the ROM checksum of your unit.
It writes `ce-16cplus.bin` (28 KB) and `ptr16c-to-B180.bin` (4 bytes).

## 5. Upload

First a run that only reads:

    powershell -ExecutionPolicy Bypass -File upload.ps1 -Dump my-15c-ce-dump.bin -Image ce-16cplus.bin -Port COM6

It checks that the calculator still equals your dump (if you used the calculator since the dump, flash
areas that hold your data may differ: put it into programming mode again and take a new dump).
Then the real run:

    powershell -ExecutionPolicy Bypass -File upload.ps1 -Dump my-15c-ce-dump.bin -Image ce-16cplus.bin -Port COM6 -Execute

It writes the image over the copy of the 15C ROM that the 15.2 mode uses (flash 0xB180-0x1217F),
page by page, saving every page before it is changed, reading it back and restoring it on a mismatch;
verifies the whole slot; then writes the pointer that makes the 16 mode use the image, and verifies
that. It takes about 25 minutes (every one of the 56 pages is saved, written and read back, about 25 seconds each; measured on the tried calculator). Keep the cable and the batteries in place until it says "Done".

## 6. Back to normal mode

HP's steps: press `RESET` on the cable (or poke a paperclip into the RESET hole in the back of the
calculator), press `ON`, "Pr Error" appears, press any key.

## 7. Using it

Calculator off. Hold `g` and `ENTER`, press `ON`: the display shows `1.L 2.C 3.H` (a test menu). Press
`4`: `15  15.2  16`. Then:

- `e^x`: the factory 15 mode (unchanged);
- `7`: the 16 mode, now the 16C+;
- `y^x`: the 15.2 mode, now also the 16C+ (the 15C with more registers is gone until you undo).

Entering the 16 mode clears the memory ("Pr Error", press `ENTER`). An overlay with the 16C key
labels helps, the keys are the same ones. The 16C+ works as the HP-16C up to 64 bits. Set a bigger
word size with the number in X and `f WSIZE` (the `STO` key), for example `DEC` `1` `2` `8` `f WSIZE`;
`f WINDOW` and a digit (the `ENTER` key) shows 8-digit windows of a long number. A first check:

- `DEC` `6` `5` `f WSIZE` shows `0 d` (the stock 16C shows `Error 2`);
- `HEX` `F` x16 (the top-row `F` key), `ENTER`, `1`, `+`, `f WINDOW` `2` shows `1`.

The simulator of this release (VoyagerSim) runs the same patch: `ROM > Apply a patch...` with
`16cplus.patch` on the 16C. `examples\16cplus-test-sheet.txt` is its test sheet.

## 8. Undo

Programming mode again (step 2), then:

    powershell -ExecutionPolicy Bypass -File undo.ps1 -Dump my-15c-ce-dump.bin -Port COM6 -Execute

It writes the original 15C ROM copy and the original pointer back from your dump and verifies both
(also about 25 minutes; tried on the real calculator).
Without `-Execute` it only reads and tells what differs.

## What is known about the 15C CE firmware (for the curious)

The CE firmware runs the Nut code of the 15C and of the 16C in an ARM emulation. Two of its
instructions differ from Nonpareil's: `c=stk` returns `address - base/2` instead of the address, and
`goto c` stays in the current 256-word page. The patch avoids both. The words of the 15C ROM copies
and the pointer literals are addressed as `base + 2 * address`; the kit changes the base of the 16 mode
from the original 16C ROM (0x12180, only 6144 words) to a 28 KB slot that also holds the second ROM
range 0x2000-0x37FF the 16C+ needs.

## Thanks

J-F Garnier for the challenge and the answers to our questions, Amphitryon for testing the patch in NutEm/PC and
VoyagerSim and for two bug reports (FLOAT n above 64 bits, BSP in float mode), both fixed in this
version.
