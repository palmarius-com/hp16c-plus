import sys
for fn in sys.argv[1:]:
    rom={}
    for l in open(fn):
        l=l.split('#')[0].strip()
        if ':' in l:
            a,v=l.split(':'); rom[int(a,16)]=int(v,16)
    addrs=sorted(rom); print(fn, 'words', len(rom), 'range %04x-%04x'%(addrs[0],addrs[-1]))
    # runs of zero words, length >= 4
    runs=[]; start=None
    for a in addrs+[None]:
        z = a is not None and rom[a]==0 and (start is None or a==prev+1)
        if z and start is None: start=a
        if not z and start is not None:
            runs.append((start,prev)); start=None
            if a is not None and rom[a]==0: start=a
        prev=a if a is not None else prev
    big=[(s,e) for s,e in runs if e-s+1>=4]
    print(' zero runs >=4:', ', '.join('%04x-%04x(%d)'%(s,e,e-s+1) for s,e in big))
    print(' total zero words', sum(1 for v in rom.values() if v==0), 'in runs>=4', sum(e-s+1 for s,e in big))
    # checksum per 1K block (Voyager self-test style: sum mod 1023)
    for b in range(addrs[0], addrs[-1]+1, 0x400):
        blk=[rom.get(a,0) for a in range(b,b+0x400)]
        print('  blk %04x sum%%1023=%d'%(b, sum(blk)%1023), end='')
    print()
    print(' whole sum%1023 =', sum(rom.values())%1023)
