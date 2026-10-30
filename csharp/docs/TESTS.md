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

## Key names

Write the keys as they are printed on the calculator, separated by spaces:

- Main keys: `ENTER`, `CHS`, `STO`, `A`, `7`, `+`, `-`, `*`, `/`, `.`
- Gold and blue functions: just their name, the app presses `f` or `g` for
  you: `WSIZE`, `FLOAT`, `LSTx`. You can also write `f WSIZE`.
- Numbers can stay together: `12AB` is 1, 2, A, B.
- A legend with spaces gets an underscore: `lin_est_x`.
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

Save your tests with Save..., and share the file; it holds no firmware.
`docs/examples/16cplus-test-sheet.txt` is a complete example: 77 tests for
the 16C+.
