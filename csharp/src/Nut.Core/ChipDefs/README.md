# Nonpareil chip definitions

The `*.ncd.tmpl` files (HP-10C, 11C, 12C, 15C, 15C-192, 16C) are copied
from Eric Smith's Nonpareil (https://github.com/brouhaha/nonpareil,
`ncd/<model>/`), Copyright 2007, 2008, 2022 Eric Smith, licensed "GPLv3
only (no later version)". They describe the chips, keyboard and LCD of
the calculators, with the key legends as comments; they contain no HP
firmware. The ROMs are not shipped: the user loads them from Nonpareil
or from a file (see `RomImage`, `ModelCatalog`).

One change to the copies: in `10c.ncd.tmpl` the legend comments of keys 26-20
were off by one (found by Thomas Klemm on the MoHPC forum; the firmware
confirms EEX, 4, 5, 6, ×). Only comments changed, no key mapping.
