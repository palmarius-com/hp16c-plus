# Voyager simulator: command API

For tests and scripts. The app only listens when you start it with
`--api` (optionally a pipe name; default `voyager-sim`):

```sh
VoyagerSim --api
VoyagerSim --api mypipe --model 15c
```

It is a local named pipe, nothing goes over the network. Send one command
per line, UTF-8; each gets one answer line, starting with `ok` or `error`.
Line breaks in an answer come as `|`. Commands run on the window's thread
through the same code as the mouse and keyboard.

| Command | Does | Answer after `ok` |
|---|---|---|
| `download ID` | what "Get ROM from Nonpareil" does | where it was saved |
| `start ID` | start `10c`...`16c` or one of your calculators; no dialogs, changes are dropped | the model |
| `patch FILE` | apply a patch to the running calculator | number of patches |
| `load-rom FILE` | what "Load full ROM file" does, without the dialogs: an original ROM is recognised, any other runs on the current calculator's keyboard and display (the 16C if none runs) | the status line |
| `fix-checksum` | what the checksum popup does on Yes: one word per off chip, as a patch called "checksum update" | the change, or `nothing to fix` |
| `unpatch` | remove all patches | |
| `testrun FILE` | run a test file (docs/TESTS.md) on a copy of the running calculator (ROM and patches, own memory); all failures go to `FILE.failures` | counts, first failures |
| `chips V` | chip layout: `own`, `large` or `preset`; restarts the calculator | the status line |
| `label [TEXT]` | set the faceplate label (`auto` = automatic); no text: read it | the label |
| `message` | the status line at the bottom of the window | the text |
| `skin [FILE]` | load a skin file (`auto` = automatic); no file: read which | the skin's name |
| `skin-save FILE` | write the current skin as JSON | |
| `changes` | what the user changed on this calculator (`;` separated) | the list, empty if nothing |
| `save-my SHORT [NAME]` | save as one of your calculators, no dialog; refuses a taken or invalid short name | the short name |
| `save-over SHORT [NAME]` | the same, replacing an existing calculator | the short name |
| `delete-my NAME` | delete one of your calculators | |
| `menu` | entries of the Calculator menu (`;` separated) | the list |
| `rominfo` | ROM, patches, chip layout, checksums | the text |
| `down CODE` | press a key (HP keycode, e.g. 41 = ON, 36 = ENTER) | |
| `chord FIRST SECOND` | two keys together, as in the right-click menu | |
| `up` | release all keys | |
| `poll` | calculator state | finished, keys taken, awake, display on (0/1), cycles |
| `display` | LCD as text | the text |
| `flags` | lit annunciators | their names |
| `status` | status panel text | the text |
| `panelview V` | status panel view: `w`, `64` or `128` | |
| `save-state FILE` | write the state as JSON | |
| `load-state FILE` | load a state; `different-rom` instead of `ok` if the ROM differs | |
| `load-state-anyway FILE` | load it although the ROM differs | |
| `export-patch FILE` | write the words that differ from the original ROM | |
| `export-rom FILE` | write the full ROM (contains HP's firmware), no dialog | |
| `screenshot FILE` | save the window as PNG | |
| `quit` | close the app | |

A key press is `down` then `up`, then `poll` until "finished" is 1. ON + key:
`down 41`, `down 20`, `up`.

`csharp/tests/App.Flow` shows a client: with `--window PATH-TO-EXE` it plays
a test sheet through the real window.
