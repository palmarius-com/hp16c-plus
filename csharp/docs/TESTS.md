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

- `hp10c-handbook.txt`: 535 tests from the examples of the HP-10C Owner's
  Handbook, about a minute on the HP-10C. All pass on the original ROM.
  Where our notes and the firmware disagreed, the firmware decided.
- `16cplus-test-sheet.txt`: 77 tests for the 16C+. They need the 16C+; on an
  original HP-16C most of them fail, as they should.
