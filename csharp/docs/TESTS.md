# How to write a test

Help > Test window lets you write down key sequences and what the display
must show, and run them with one click. It always starts with a fresh copy
of the calculator you are running, so your calculator and its memory are
not touched.

## One line, one test

```
1 2 ENTER 3 0 +  =>  42 h
```

Left of `=>` the keys to press, right of it what the display must show.
That example is for the 16C, which starts in HEX: 12 + 30 = 42. Press Run: a
green `ok` line means it matched; an orange `FAIL` line shows what the
display said instead. The next line goes on from there, so tests can build
on each other.

While tests run, both sides of the window follow along: the newest result
stays at the bottom on the right, the line it came from at the bottom on the
left. At the end the window says `Finished:` with the counts, or `Stopped:`
if you pressed Stop.

## Key names

Write the keys as they are printed on the calculator, separated by spaces:

- Main keys: `ENTER`, `CHS`, `STO`, `A`, `7`, `+`, `-`, `*`, `/`, `.`
- Gold and blue functions: just their name, the app presses `f` or `g` for
  you: `WSIZE`, `FLOAT`, `LSTx`. You can also write `f WSIZE`.
- Numbers can stay together: `12AB` is 1, 2, A, B.
- A legend with spaces gets an underscore: `lin_est_x`, `LAST_X`.
- Under a gold bracket, write only the word above the key: `PRGM`, `REG`,
  `PREFIX`, not `CLEAR`.
- Upper or lower case does not matter, unless two keys differ only by that
  (on the 16C, `F` is the hex digit, `f` the gold key).

An unknown name shows as `FAIL` with the name it did not know.

## Everything a line can do

| Line | What it does |
|---|---|
| `KEYS => TEXT` | press the keys, the display must show exactly TEXT |
| `KEYS` | only press the keys |
| `=> TEXT` | only check the display |
| `KEYS => TEXT [C -G]` | also annunciators: C must be lit, G must not be |
| `KEYS => (blank)` | the display must be off |
| `hold KEYS => TEXT` | keep the last key down while checking (for STATUS) |
| `chord K => TEXT` | hold ON, press K, release; the calculator must be off |
| `reset` | memory reset: off, ON + -, clear Pr Error |
| `# text` | comment |

Blank lines are ignored.

## Examples

Word size and STATUS on the 16C, while the key is held:

```
reset
2 0 WSIZE
hold STATUS  =>  2-32-0000
```

The self-test:

```
ON  =>  (blank)
chord *  =>  -8,8,8,8,8,8,8,8,8,8,  [C G]
```

## Programs

Tests run at full speed, and a line waits until the calculator is idle again.
So a value shown by PSE is gone before a test can read it, and a program that
only stops on a key never lets the next line start: after 60 seconds the test
moves on. Check what a program leaves in the display when it stops.

## Example files

Save your tests with Save..., and share the file; it holds no firmware.

Two example files come with the program, in the `examples` folder of the
download and in `docs/examples` of the source. Load them with Open...:

- `hp10c-handbook.txt`: 536 tests from the examples of the HP-10C Owner's
  Handbook, about a minute on the HP-10C. The expected values are the
  handbook's. A row marked "ROM only" shows what the original ROM displays
  in the simulator, where the handbook prints no value. One row fails and is
  meant to: `GTO 8 5 => Error 4`. The handbook names Error 4 for line
  numbers above 79, the ROM shows none in that state. It is not yet known
  if that is the simulator or the test.
- `hp11c-handbook.txt`, `hp12c-handbook.txt`, `hp15c-handbook.txt`,
  `hp16c-handbook.txt`: the examples of the Owner's Handbook of each
  calculator (407, 654, 547 and 253 checks). The expected values are the
  handbook's, read from the scanned pages by a model; no person has proofread
  them. Each file runs on its own calculator. Lines starting with `##` are not
  run: their check fails in the simulator, and the reason is below each. They
  are findings, the cause is not known yet. The examples that run a program
  from the handbook's listings are only partly in: the listings are not keyed
  in yet.
- `16cplus-test-sheet.txt`: 77 tests for the 16C+. They need the 16C+; on an
  original HP-16C most of them fail, as they should.

A passing test shows that the display matches its expected value, no more.
Where an expected value comes from is part of the test: a value taken from
the simulator itself proves nothing about the simulator.
