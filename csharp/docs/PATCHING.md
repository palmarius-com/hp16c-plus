# How to write a patch

This guide starts with what a patch is and ends with your own code in
the calculator. You need no special tools for the first parts: a text
editor and the simulator are enough.

## 1. What a ROM is

The calculator's firmware is a list of numbers, the ROM. Each number is
a "word" of 10 bits (0 to 1023, written in hex as `000` to `3FF`), and
each word has an address (written in hex too, like `01F4`). The
processor, HP's "Nut", reads the words one after the other and does
what they say.

A Nonpareil ROM file is plain text, one word per line:

```
01f4:399
01f5:0da
```

means: the word at address `01F4` is `399`, at `01F5` it is `0DA`.

## 2. What a patch is

A patch file looks exactly the same, but it lists only the words you
change. The simulator loads the original ROM, then puts your words on
top. Lines starting with `#` are comments.

```
# my first patch
2000:00e
2001:3e0
```

Why patches and not whole ROMs? The ROM belongs to HP; your patch is
only your own work. You can share a patch freely, and everybody applies
it to their own copy of the original.

## 3. Try it

1. Start a calculator (for example the HP-16C).
2. Save the two lines above as `first.patch` with any text editor.
3. ROM > Apply a patch..., choose `first.patch`.
4. ROM > ROM information shows the patch, that the large chip layout
   is preset (the 16C has no ROM at `2000`), and that a checksum is off.
5. ROM > Remove all patches undoes it.

Nothing breaks: the words at `2000` are never run, because no original
code jumps there. That is the next step.

## 3b. A real fix in ten words: the CHS stack-lift bug

Your first useful patch can be tiny. The HP-11C, 15C and 16C share a
small bug, known on the MoHPC forum: when X is 0, CHS does not switch
stack lift back on. After CLx (or BSP) the next number then overwrites X
instead of pushing the stack. Thomas Klemm found it and wrote the first
patches in 2015 (MoHPC forum, thread 5321, "HP-15C: Fixing the CHS and Stack
Lift Bug"). Amphitryon checked the 11C patch against a firmware trace and
corrected its jumps. The 16C fix below reached us through him; he says he
completed it, and we would not have known without him.
Which word is whose, we cannot tell.

**See the bug** on the original 16C. Floating-point mode shows it:

```
f FLOAT .          floating-point mode
3 ENTER 4          Y = 3, X = 4
g CLx              X = 0, stack lift off (as it should be)
CHS                X = -0; this should switch stack lift on again
7 +                original 16C: 1.000000 01 (it computed 3 + 7)
```

CHS is an operation like any other, so the 7 should go on top of the
stack and the result should be 7 (-0 + 7). Without the CHS, 10 is right.

**Apply the fix.** The patch is ten lines. Copy them into a text file
(e.g. `16c-chs-stack-lift.patch`), or take the ready file from the
project: `csharp/docs/examples/16c-chs-stack-lift.patch`.

```
# HP-16C: CHS stack-lift fix. Credit: Thomas Klemm found the bug and wrote the first patches
# (HP-15C 2015-12-12, HP-11C and HP-16C 2015-12-14; MoHPC forum, thread 5321 "HP-15C: Fixing the
# CHS and Stack Lift Bug"). Amphitryon checked the 11C patch against a firmware trace and
# corrected its jumps (same thread), passed this 16C version on to us and says he completed it;
# which word is whose we cannot tell.
006f:390
0c8c:183
0c96:137
0caf:05b
0cb8:01b
0cb9:2be
0cba:0f0
0cbc:251
0cbd:002
0ce9:28b
```

1. Start the HP-16C.
2. ROM > Apply a patch..., choose the file.
3. Type the same keys again: now the result is `7.000000 00`. The same
   check as a test file: `csharp/docs/examples/16c-chs-stack-lift-test-sheet.txt`
   fails on the original 16C (2 lines) and passes with the patch.
4. ROM > ROM information: the checksum of chip 0000-17FF is still ok.

**What the ten words do.** Nine of them (0C8C to 0CE9) change jumps in
the original CHS code so that CHS clears the
"stack lift disabled" flag also when X is 0. The tenth, at 006F, is
0 in the original ROM; it is set so the chip still adds up to 1
(see section 5), and the self-test stays happy. Nothing new is added
to the ROM: the patch fits in the 16C's own chip, so the simulator does
not need the larger 15C layout.

To keep it: Calculator > Save as my calculator, name it "HP-16C (CHS fix)".


## 4. Where there is room

| Calculator | Own ROM | Free for patches |
|---|---|---|
| 10C | 0000-0FFF | 2000-37FF (large layout) |
| 11C, 12C, 16C | 0000-17FF | 2000-37FF (large layout) |
| 15C | 0000-17FF and 2000-37FF | almost nothing; replace code |

"Large layout" means the simulator presets the 15C's chips for your ROM
(ROM > Chip layout lets you change that): a second ROM chip at `2000-37FF` (6144 free words) and
the 15C's extra memory. On a real calculator this is the HP-15C
hardware (for example the 15C Collector's Edition), which is how the
16C+ runs.

## 5. Checksums

Each ROM chip has a checksum: all its words added up, divided by 1023,
must leave 1. The calculator's self-test (hold ON, press ×, release ON)
checks this and shows `Error 9` (or similar) if it is wrong. The
calculator still works; only the self-test complains.

To keep the self-test happy, pick one word in the chip that nothing
uses and set it so the sum comes out right. `tools/make_patch.py`
(next section) does the arithmetic for you.

## 6. Writing code: the assembler

Writing words by hand gets old fast. The project includes an assembler
(`nut/asm.py`) that turns readable instructions into words, and a tool
that writes the patch file:

```
; demo.asm
org $2000          ; put the following at address 2000
start:  a=0 w      ; clear register A
        rtn        ; return
```

```sh
python tools/make_patch.py demo.asm -o demo.patch
python tools/make_patch.py demo.asm -o demo.patch --rom nonpareil/16c.rom --checksum 37ff:2000-37ff
```

The second line also sets the checksum word `37FF` for the chip
`2000-37FF`. Get the original ROMs for the tools with
`python tools/fetch_nonpareil.py` (from https://github.com/brouhaha/nonpareil).

The instruction names are Nonpareil's, as printed by the disassembler:

```sh
python nut/disasm.py nonpareil/16c.rom --range 0000-00ff
```

Reading the original code this way is how you find where to hook in.
For the 16C, `re/16c.sym` gives names and comments for many routines.

## 7. Hooking into the original code

Your code only runs if the original firmware jumps to it. The usual
way:

1. Find the place in the original code where your change belongs.
2. Replace two words there with a long jump to your code
   (`golong my_code`, 2 words) or a long call (`gosub my_code`).
3. In your code, first do what the two replaced words did, then your
   own work, then jump back (`golong` to the address after the hook)
   or return (`rtn`).
4. Put the hook words into the same patch (`org $0372` then the
   `golong`).
5. Fix the checksums of both chips: the original chip changed too.

Keep a list of your hooks; the 16C+ source (`plus/src/16cplus.asm`)
is a complete, commented example with 21 hook and checksum words in
the 16C ROM and about 5700 words of new code.

## 8. Testing

- Apply the patch in the simulator and use the calculator.
- The status panel shows the program counter and the last addresses
  run: if your code at `2000` runs, you see `20xx` there.
- ROM > Export full ROM, then load it in another emulator (NutEm/PC,
  Nonpareil) for a second opinion.
- Run the self-test (ON + ×) to check the checksums.
- Only then try a real calculator.

When the patch is done, Calculator > Save as my calculator keeps the patched
calculator with its own Start button under "Your calculators" (see the
user guide), so you and your testers do not have to apply the patch by
hand every time. The HP-16C+ works the same way: start the 16C, apply
the patch, save it, and it is your first own calculator.

## 9. Sharing

Publish the patch file and your source, never an exported full ROM
(it contains HP's firmware). Write at the top of the patch which
original ROM it is for; the simulator's ROM > ROM information shows the
original's fingerprint (SHA-256), which identifies it exactly.
