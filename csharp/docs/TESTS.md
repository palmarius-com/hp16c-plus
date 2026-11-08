# How to write a test

Help > Test window lets you write down key sequences and what the display
must show, and run them with one click. It runs on a copy of the calculator you
are running: the same ROM and patches, its own memory, so your calculator
and its memory are not touched.

## One line, one test

```
1 2 [ENTER] 3 0 [+]  =>  '42 h'
```

Left of `=>` the keys to press, right of it, in ticks, what the display must
show. That example is for the 16C, which starts in HEX: 12 + 30 = 42.

- Each key is one pair of brackets, as the handbooks draw their keys: `[ENTER]`,
  `[+]`, `[f]`, `[GTO]`. Digits and the decimal point stay bare: `1 2`, `7 . 5`.
- Press Run. A green `ok` line means the display matched. An orange `FAIL`
  line shows both texts in ticks, so a stray space is visible:
  `[GTO] . 0 4  =>  '04-43,33 07'  failed with '04-43,33 00'`.
- The next line goes on from there, so tests can build on each other.

While tests run, both sides of the window follow along: the newest result
stays at the bottom on the right, the line it came from at the bottom on the
left. At the end the window says `Finished:` with the counts, or `Stopped:`
if you pressed Stop. Then an **Export Log** button appears next to that line: it saves the
result as a text file (calculator, test file, date, the counts, then every line with `ok` or
`FAIL`), for a bug report or to compare two runs. The Save dialog opens in the folder the test
file came from and suggests its name plus `-results-` and the date and time, for example
`hp10c-quick-tests-results-20261031_133159.txt`.

## Writing the keys

Write the keys as they are printed on the calculator, separated by spaces:

- Every key in brackets, as the handbooks print it: `[R/S]`, `[CHS]`, `[STO]`,
  `[A]`, `[×]`, `[÷]`, `[−]`. Digits and the decimal point stay bare.
- Gold and blue functions: write the function, the app presses `f` or `g`
  for you: `[WSIZE]`, `[FLOAT]`, `[LSTx]`. You may write the shift yourself, as
  the book does: `[f] [FIX] 4`.
- One bracket is one key, but a function can take two presses: `P/R` is the
  `g` function of the `R/S` key, so `[P/R]` presses `g`, then `R/S`. The
  handbook's `[g] [P/R]` is the same, and `[R/S]` alone is the plain key that
  runs a program.
- Hex digit strings on the 16C can stay together: `FFFF` is F, F, F, F. In
  brackets, `[A]` is the key A (a hex digit, or a label such as `GSB [A]`).
- A legend with spaces gets an underscore: `[lin_est_x]`, `[LAST_X]`.
- Under a gold bracket, write only the word above the key: `[PRGM]`, `[REG]`,
  `[PREFIX]`, not `CLEAR`.
- Upper or lower case does not matter, unless two keys differ only by that
  (on the 16C, `[F]` is the hex digit, `[f]` the gold key).
- The brackets are optional: `ENTER` and `[ENTER]` are the same key, so
  files written without them still run.

An unknown name shows as `FAIL` with the name it did not know.

## The text in ticks

The display is 11 cells: a sign cell (blank or `-`) and 10 digit cells. A
blank cell is a space in the text. The test cuts the blanks at the start and
at the end on both sides, so these mean the same:

```
[GTO] [A]  =>  '005-  22 A'
[GTO] [A]  =>  ' 005-  22 A'
```

Spaces inside the ticks count: `'005-  22 A'` (two blank cells between 22 and
A) is not `'005- 22 A'`. The handbooks print such displays with their own
spacing, and the calculator's gaps are not the same. When a line fails only
because of the gaps, the FAIL line shows the text the display really has;
copy it into the test.

Where an expected value comes from is part of the test: write down what the
handbook (or the calculator's manual) says, not what the simulator shows.

## Everything a line can do

| Line | What it does |
|---|---|
| `KEYS => 'TEXT'` | press the keys, the display must show exactly TEXT |
| `KEYS` | only press the keys |
| `=> 'TEXT'` | only check the display |
| `KEYS => 'TEXT'  [C -G]` | also annunciators: C must be lit, G must not be |
| `KEYS => (blank)` | the display must be off |
| `hold KEYS => 'TEXT'` | keep the last key down while checking (for STATUS) |
| `chord { [ON] [K] } => 'TEXT'` | the keys go down in the order written, each for a moment, then all come up; the calculator must be off before (a line `[ON] => (blank)` above does that). A key marked `hold` stays down for the check: `chord { [ON] hold [-] }` |
| `[ON] wait 0.5 chord { [ON] [-] } [CLx] => 'TEXT'` | a line is a list of actions, left to right, with one check at the end: keys, `wait SECONDS`, `chord { ... }` |
| `KEYS wait 0.5 => 'TEXT'` | press the keys, check 0.5 s of calculator time later, without waiting for the calculator to go idle |
| `hold KEYS wait 1 => 'TEXT'` | keep the last key down for 1 s of calculator time, then check |
| `wait 2 => 'TEXT'` | only wait 2 s of calculator time, then check |
| `reset` | memory reset: off, ON + -, clear Pr Error |
| `# text` | comment |
| `## KEYS => 'TEXT'` | a test that is switched off (see below) |

Blank lines are ignored. The ticks are optional too: `=> 42 h` works, but a
space at the wrong place is then easy to miss.

## Switching a test off

A line starting with `##` is a comment, so it does not run. The example files
use it for checks that fail in the simulator and are not understood yet: the
line stays where it was, with the reason on the line below:

```
## [GTO] . 0 4  =>  '04-43,33 07'
#     not run: the simulator shows '04-43,33 00'
```

Delete the `##` to run the line again. Skipping a line can change what later
lines see, so switch off the lines that depend on it as well.

## Examples

Word size and STATUS on the 16C, while the key is held:

```
reset
2 0 [WSIZE]
hold [STATUS]  =>  '2-32-0000'
```

The self-test:

```
[ON]  =>  (blank)
chord { [ON] [*] }  =>  '-8,8,8,8,8,8,8,8,8,8,'  [C G]
```

## Waiting

`wait SECONDS` counts calculator time: words at the real Voyager speed (215 kHz, 56 clocks per word, about
3840 words a second). The program still runs flat out, so a test stays fast and gives the same result every
run. Use it for what a person sees while a key is down or during a pause (STATUS, a held matrix key, PSE,
`running`). The wait ends early if the calculator goes to sleep.

## Programs

Tests run at full speed, and a line waits until the calculator is idle again.
So a value shown by PSE is gone before a test can read it, and a program that
only stops on a key never lets the next line start: after 60 seconds the test
moves on. Check what a program leaves in the display when it stops. A line that starts a
program which never ends costs those 60 seconds (about 3 billion calculator cycles at full
speed), so leave such a line out of a test file (`##`).

## Example files

Save your tests with Save..., and share the file; it holds no firmware.

The example files come with the program, in the `examples` folder of the
download and in `docs/examples` of the source. Load them with Open...: the
dialog starts in that folder, and later in the folder of the last file you opened.

- `hp10c-handbook-tests.txt`: 536 tests from the examples of the HP-10C Owner's
  Handbook, about a minute on the HP-10C, all pass. The expected values are the
  handbook's. A row marked "ROM only" shows what the original ROM displays
  in the simulator, where the handbook prints no value. The row about line
  numbers above 79 is `[GTO] 8 => 'Error 4'`: the handbook names Error 4 for
  them, and the calculator shows it right after the 8, because a number from
  80 up cannot be a line; the next key only clears it.
- `hp11c-handbook-tests.txt`, `hp12c-handbook-tests.txt`,
  `hp15c-handbook-tests.txt`, `hp16c-handbook-tests.txt`: the examples of the
  Owner's Handbook of each calculator (1556, 1275, 797 and 447 checks; the 15C file has 165 lines switched
  off: SOLVE, the integral and what depends on them; its matrix examples run at Real speed). The
  expected values are the handbook's, read from the scanned pages by a model;
  no person has proofread them. Each file runs on its own calculator. Lines
  starting with `##` are switched off: their check fails in the simulator,
  and the reason is below each. They are findings, the cause is not known
  yet. The handbook's program listings are keyed in as blocks between
  `# listing begin` and `# listing end` lines: each row's keycodes are checked
  against the handbook, the spacing in the quotes is the simulator's. The
  examples that follow a listing and still fail are in the same state as the
  other switched-off lines.
- `16c-chs-stack-lift-test-sheet.txt`: four lines for the HP-16C and the CHS
  stack-lift fix (`16c-chs-stack-lift.patch`, see PATCHING.md). On the
  original ROM the two lines marked BUG fail; apply the patch (ROM > Apply a
  patch...), run again, and all four pass. The expected values are worked out
  from the stack, not taken from the simulator.
- `hp15c-le-known-bugs-tests.txt`: the known bugs of the HP-15C Limited Edition (HP Articles Forum, Katie
  Wasserman, 2011), as tests on the HP-15C. The LE bugs are in the LE's own firmware, which the simulator does not
  run: each line states what the original 15C ROM does, so the simulator passes, and a calculator with the LE bug
  would fail. 16 checks pass; the self-test item and two key-timing items are switched off with the reason below
  each. The current-draw and low-battery items are hardware and not in the file.
- `16cplus-test-sheet.txt`: 77 tests for the 16C+. They need the 16C+; on an
  original HP-16C most of them fail, as they should.

A passing test shows that the display matches its expected value, no more.
Where an expected value comes from is part of the test: a value taken from
the simulator itself proves nothing about the simulator.
