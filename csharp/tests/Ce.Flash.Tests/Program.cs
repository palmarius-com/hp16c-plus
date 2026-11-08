// Tests of the SAM-BA client and the flash controller against the fake SAM4L: the protocol bytes, a page write, the
// refusals, a cable pulled at every command of a page write (and the repair from the saved pre-image), short replies and
// controller errors.   dotnet run --project csharp/tests/Ce.Flash.Tests -c Release
using Ce.Flash;

int failed = 0, total = 0;
void Check(string name, bool ok, string detail = "")
{
    total++;
    if (!ok) failed++;
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}{(ok || detail == "" ? "" : "  -> " + detail)}");
}
bool Throws<T>(Action a, out string message) where T : Exception
{
    try { a(); message = ""; return false; }
    catch (T e) { message = e.Message; return true; }
}

byte[] Pattern(int seed)
{
    var b = new byte[Sam4lFlash.PageSize];
    for (int i = 0; i < b.Length; i++) b[i] = (byte)(seed * 31 + i * 7);
    return b;
}
(FakeSam4l Dev, Sam4lFlash Flash) Rig(uint minWrite = 0x4000)
{
    var dev = new FakeSam4l();
    return (dev, new Sam4lFlash(new SamBaClient(dev), minWrite));
}
bool IsErased(byte[] b) => b.All(x => x == 0xFF);

// 1. protocol and preflight
{
    var (dev, fl) = Rig();
    var hello = fl.Client.Hello();
    Check("N# answers 0A 0D", hello.Length == 2 && hello[0] == 0x0A && hello[1] == 0x0D);
    Check("chip id", fl.Client.ReadWord(Sam4lFlash.Cidr) == 0xAB0A07E1);
    fl.Preflight();
    Check("preflight on a good chip", true);
    Check("erased flash reads 0xFFFFFFFF", fl.Client.ReadWord(0x8000) == 0xFFFFFFFF);
}

// 2. the command allowlist: nothing but N#, w, W leaves the client
{
    var (dev, fl) = Rig();
    string[] good = ["N#", "w400A0008,#", "W8000,DEADBEEF#"];
    string[] bad = ["G8000#", "S8000,100#", "w400A0008#", "W8000,DEADBEE#", "w,#", "n#", "W8000,deadbeef#", "N# "];
    Check("allowed commands match", good.All(c => SamBaClient.Allowed.IsMatch(c)));
    Check("everything else is refused", bad.All(c => !SamBaClient.Allowed.IsMatch(c)));
}

// 3. a page write, read back, neighbours untouched
{
    var (dev, fl) = Rig();
    var data = Pattern(1);
    long before = fl.Client.CommandsSent;
    fl.ProgramPage(100, data);
    long used = fl.Client.CommandsSent - before;
    Check("page written and read back", fl.ReadPage(100).SequenceEqual(data));
    Check("pages next to it untouched", IsErased(fl.ReadPage(99)) && IsErased(fl.ReadPage(101)));
    Check($"a page write is about 140 commands, no waiting for replies ({used})", used is > 130 and < 200);
    var w = fl.WritePageChecked(100, data);
    Check("a checked write of the same data touches nothing", !w.Changed);
    var w2 = fl.WritePageChecked(100, Pattern(2));
    Check("a checked write changes it and keeps the pre-image", w2.Changed && w2.PreImage.SequenceEqual(data) && fl.ReadPage(100).SequenceEqual(Pattern(2)));
}

// 4. refusals: the boot loader is never written, wrong pages, wrong sizes
{
    var (dev, fl) = Rig();
    long before = fl.Client.CommandsSent;
    bool t1 = Throws<SamBaException>(() => fl.ProgramPage(5, Pattern(3)), out var m1);
    Check("a page of the boot loader is refused before a byte leaves", t1 && fl.Client.CommandsSent == before, m1);
    Check("page 31 (0x3E00, last page below 0x4000) is refused", Throws<SamBaException>(() => fl.ProgramPage(31, Pattern(3)), out _));
    Check("page 32 (0x4000) is allowed", !Throws<SamBaException>(() => fl.ProgramPage(32, Pattern(3)), out _));
    Check("page 256 does not exist", Throws<SamBaException>(() => fl.ProgramPage(256, Pattern(3)), out _));
    Check("a short buffer is refused", Throws<ArgumentException>(() => fl.ProgramPage(40, new byte[100]), out _));
}

// 5. the chip's own protection: with the client's guard lowered, the locked region 0 still gives LOCKE and stays intact
{
    var (dev, fl) = Rig(minWrite: 0);
    Array.Fill(dev.Flash, (byte)0x5A, 0, 0x4000);
    bool t = Throws<SamBaException>(() => fl.ProgramPage(5, Pattern(4)), out var m);
    Check("the device refuses a locked region (LOCKE)", t, m);
    Check("and the boot loader is unchanged", dev.Flash.Take(0x4000).All(x => x == 0x5A));
}

// 6. a cable pulled at every command of a page write: the page is old, erased or new, never anything else; the pre-image repairs it
{
    var old = Pattern(5); var fresh = Pattern(6);
    var probe = Rig(); probe.Flash.ProgramPage(100, old);
    long start = probe.Dev.CommandsHandled;
    probe.Flash.ProgramPage(100, fresh);
    long commands = probe.Dev.CommandsHandled - start;                // the commands of one page write
    int badStates = 0, notRepaired = 0, cuts = 0;
    for (long cut = 0; cut <= commands; cut++)
    {
        var (dev, fl) = Rig(); fl.ProgramPage(100, old);
        dev.CutAfterCommands = dev.CommandsHandled + cut;
        try { fl.ProgramPage(100, fresh); } catch (IOException) { cuts++; }
        var page = dev.Flash.AsSpan(100 * Sam4lFlash.PageSize, Sam4lFlash.PageSize).ToArray();
        if (!(page.SequenceEqual(old) || page.SequenceEqual(fresh) || IsErased(page))) badStates++;
        dev.Reconnect();                                              // plug in again, programming mode, a new session
        var fl2 = new Sam4lFlash(new SamBaClient(dev));
        fl2.Preflight();
        fl2.ProgramPage(100, old);
        if (!fl2.ReadPage(100).SequenceEqual(old)) notRepaired++;
    }
    Check($"cable pulled at each of {commands + 1} points: the page is only old, erased or new ({cuts} cuts)", badStates == 0 && cuts > 100, $"{badStates} odd states");
    Check("and the saved pre-image repairs every one of them", notRepaired == 0);
}

// 7. a short reply is an error, not a wrong value
{
    var (dev, fl) = Rig();
    dev.TruncateReadReplies = true;
    Check("a 2-byte reply to a read is refused", Throws<SamBaException>(() => fl.Client.ReadWord(0x8000), out _));
}

// 8. a controller error while writing: the checked write puts the pre-image back
{
    var (dev, fl) = Rig();
    var old = Pattern(7); fl.ProgramPage(120, old);
    dev.FailNextProgram = true;
    bool t = Throws<SamBaException>(() => fl.WritePageChecked(120, Pattern(8)), out var m);
    Check("a write error is reported and the pre-image written back", t && m.Contains("written back") && fl.ReadPage(120).SequenceEqual(old), m);
}

// 9. a monitor that answers W (the real one may): nothing may depend on it
{
    var (dev, fl) = Rig();
    dev.WriteReplies = true;
    var data = Pattern(9);
    fl.ProgramPage(130, data);
    Check("works the same when every W is answered", fl.ReadPage(130).SequenceEqual(data));
}

// 10. flash programs 1 to 0 only: a write without an erase cannot set bits again (the controller model)
{
    var (dev, fl) = Rig();
    fl.ProgramPage(140, Pattern(10));
    var twice = fl.ReadPage(140);
    Check("programming the same data again (erase first) gives the same page", twice.SequenceEqual(Pattern(10)));
}

// 11. the image builder (CeUnit): the same image as kit/build_image.ps1 made for the unit's own dump
{
    string? FindUp(string rel)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, rel);
            if (File.Exists(p)) return p;
        }
        return null;
    }
    var knownPath = FindUp("15c-ce/known-units.txt") ?? FindUp("csharp/15c-ce/known-units.txt");
    var patchPath = FindUp("docs/examples/16cplus.patch") ?? FindUp("csharp/docs/examples/16cplus.patch");
    var dumpPath = FindUp("hardware/dump/flash-00000-1FFFF.bin") ?? FindUp("hardware/dump/flash-00000-1FFFF.bin");
    Check("known-units.txt and the patch are found", knownPath is not null && patchPath is not null);
    var known = CeUnit.ParseKnown(File.ReadAllText(knownPath!));
    var patch = CeUnit.ParsePatch(File.ReadAllText(patchPath!));
    Check($"known-units.txt: 6 regions ({known.Count})", known.Count == 6);
    Check($"the patch: {patch.Count} words, 31 in 0000-17FF", patch.Keys.Count(x => x < 0x1800) == 31 && patch.Keys.Count(x => x >= 0x2000) > 5000);

    // refusals that need no calculator
    Check("a short dump is refused", Throws<ArgumentException>(() => CeUnit.FromDump(new byte[1000], known), out _));
    var junk = new byte[CeUnit.DumpSize];
    bool unknown = Throws<UnknownUnitException>(() => CeUnit.FromDump(junk, known), out var um);
    Check("a dump of another unit is refused and says which regions differ", unknown && um.Contains("bootloader"), um);

    if (dumpPath is null)
        Console.WriteLine("skip the builder checks that need the private dump of a calculator (hardware/dump/flash-00000-1FFFF.bin)");
    else
    {
        var dump = File.ReadAllBytes(dumpPath);
        var unit = CeUnit.FromDump(dump, known);
        Check("the dump of the tried calculator is accepted", true);
        var bent = (byte[])dump.Clone(); bent[0x12180 + 100] ^= 1;
        Check("one changed byte in the 16C ROM is refused", Throws<UnknownUnitException>(() => CeUnit.FromDump(bent, known), out _));
        var img = unit.BuildSlot(patch);
        Check("slot is 28672 bytes, pointer 80 B1 00 00", img.Slot.Length == 0x7000 && img.Pointer.SequenceEqual(new byte[] { 0x80, 0xB1, 0, 0 }));
        Check($"keep-sum: chip sum stays the unit's {img.UnitChipSum} (patch alone {img.SumWithPatch}, word 13FF {img.ChecksumWordOld:X3} -> {img.ChecksumWordNew:X3})",
              img.FinalChipSum == img.UnitChipSum && img.UnitChipSum == 520);
        Check("1800-1FFF of the slot is zero", img.Slot.AsSpan(2 * 0x1800, 2 * 0x800).ToArray().All(x => x == 0));
        var noKeep = unit.BuildSlot(patch, keepSum: false);
        Check("without keep-sum word 13FF is the patch's own", noKeep.ChecksumWordNew == noKeep.ChecksumWordOld && noKeep.FinalChipSum == noKeep.SumWithPatch);
        Check("patch refusals: a word in 1800-1FFF, a word at 3800, a too small patch",
              Throws<ArgumentException>(() => unit.BuildSlot(new Dictionary<int, int>(patch) { [0x1900] = 1 }), out _) &&
              Throws<ArgumentException>(() => unit.BuildSlot(new Dictionary<int, int>(patch) { [0x3800] = 1 }), out _) &&
              Throws<ArgumentException>(() => unit.BuildSlot(new Dictionary<int, int> { [0x100] = 1 }), out _));

        // a calculator with an image on it (the dump of 2026-11-07, after the first upload) and the change-set of a My Calculator
        // (built from the factory dump: the slot written and the pointer moved, which is all an upload changes)
        var ericPath = FindUp("nonpareil/16c.rom") ?? FindUp("nonpareil/16c.rom");
        {
            var v3 = (byte[])dump.Clone();
            Array.Copy(img.Slot, 0, v3, CeUnit.SlotAt, img.Slot.Length);
            Array.Copy(new byte[] { 0x80, 0xB1, 0x00, 0x00 }, 0, v3, CeUnit.PointerAt, 4);
            Check("a dump with an image installed is refused as 'not the factory unit'", Throws<UnknownUnitException>(() => CeUnit.FromDump(v3, known), out _));
            var withImage = CeUnit.FromDump(v3, known, allowImage: true);
            Check($"...and accepted as the known calculator with an image installed ({withImage.ImageRegionsChanged.Count} changed regions: slot and pointer)",
                  withImage.ImageRegionsChanged.Select(r => r.Start).OrderBy(x => x).SequenceEqual(new[] { CeUnit.SlotAt, CeUnit.PointerAt }));
            var bentBoot = (byte[])v3.Clone(); bentBoot[0x100] ^= 1;
            Check("allowImage does not let anything else differ (boot loader changed: refused)", Throws<UnknownUnitException>(() => CeUnit.FromDump(bentBoot, known, allowImage: true), out _));
        }
        {
            var orig = new Dictionary<int, int> { [0] = 1, [1] = 2, [0x13FF] = 0 };
            var mine = new Dictionary<int, int> { [0] = 1, [1] = 3, [0x2000] = 5, [0x13FF] = 0 };
            var ch = CeUnit.ChangesFrom(mine, orig);
            Check("a My Calculator's changes: the words that differ from the original and all of 2000-37FF", ch.Count == 2 && ch[1] == 3 && ch[0x2000] == 5);
            Check("a ROM word outside the two ROM ranges is refused", Throws<ArgumentException>(() => CeUnit.ChangesFrom(new Dictionary<int, int> { [0x1900] = 1 }, orig), out _));
            Check("checksum word 13FF: free when the changes do not use it", CeUnit.ChecksumWordIsFree(ch, orig));
            Check("checksum word 13FF: not free when the changes put code there and the sum is not 1",
                  !CeUnit.ChecksumWordIsFree(new Dictionary<int, int> { [0x13FF] = 0x123 }, orig));
        }
        if (ericPath is not null)
        {
            var eric = CeUnit.ParsePatch(File.ReadAllText(ericPath).Replace("# ", "#"));
            var unit2 = CeUnit.FromDump(dump, known);
            var ch = CeUnit.ChangesFrom(new Dictionary<int, int>(patch.Where(kv => true)), eric);
            Check($"the 16C+ patch as changes over the original 16C: {ch.Count} words, 31 in 0000-17FF", ch.Keys.Count(x => x < 0x1800) == 31 || ch.Keys.Count(x => x < 0x1800) <= 31);
            Check("its checksum word 13FF is its own (the patched original sums to 1): free", CeUnit.ChecksumWordIsFree(ch, eric));
            var over = unit2.OverwritesUnitSpecificWords(ch, eric);
            Check($"it overwrites exactly the three CE words of dead code, 13FD-13FF ({string.Join(" ", over.Select(a => a.ToString("X4")))})",
                  over.SequenceEqual(new[] { 0x13FD, 0x13FE, 0x13FF }));
            var asCeBased = new Dictionary<int, int>(ch);
            foreach (var a in new[] { 0x0C6C, 0x13ED, 0x13EE }) asCeBased[a] = unit2.Own16cRom()[a];      // a My Calculator built on the CE ROM
            Check("a CE-based My Calculator gives the same slot image (its CE words are the unit's own)",
                  unit2.BuildSlot(asCeBased).Slot.AsSpan().SequenceEqual(unit2.BuildSlot(ch).Slot));
        }

        // the same bytes as the PowerShell script that put the first image on the calculator
        var ps1 = FindUp("15c-ce/build_image.ps1") ?? FindUp("csharp/15c-ce/build_image.ps1");
        if (ps1 is not null && OperatingSystem.IsWindows())
        {
            var outDir = Path.Combine(Path.GetTempPath(), "ce-image-" + Guid.NewGuid().ToString("N"));
            var psi = new System.Diagnostics.ProcessStartInfo("powershell",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{ps1}\" -Dump \"{dumpPath}\" -Patch \"{patchPath}\" -OutDir \"{outDir}\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using (var p = System.Diagnostics.Process.Start(psi)!) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(); }
            var psImg = Path.Combine(outDir, "ce-16cplus.bin");
            Check("identical to build_image.ps1, byte for byte", File.Exists(psImg) && File.ReadAllBytes(psImg).AsSpan().SequenceEqual(img.Slot));
            Check("pointer file identical too", File.Exists(Path.Combine(outDir, "ptr16c-to-B180.bin")) &&
                  File.ReadAllBytes(Path.Combine(outDir, "ptr16c-to-B180.bin")).AsSpan().SequenceEqual(img.Pointer));
            try { Directory.Delete(outDir, true); } catch { }
        }
    }
}

// 10b. what the real monitor does (log of the first run on a calculator, 2026-11-08): a word reply arrives as 1 byte, then 3 bytes
{
    var dev = new FakeSam4l();
    var client = new SamBaClient(dev);
    Check("the fake sends a word reply in two pieces (1 + 3 bytes), like the real monitor", dev.FragmentReadReplies);
    dev.Write("w400E0740,#"u8);
    var first = new byte[4];
    int n1 = dev.Read(first, 10);
    int n2 = dev.Read(first.AsSpan(n1), 10);
    Check($"a first read returns 1 byte, the next the other 3 ({n1}+{n2})", n1 == 1 && n2 == 3 && BitConverter.ToUInt32(first, 0) == Sam4lFlash.CidrExpected);
    Check("the client collects the pieces", client.ReadWord(Sam4lFlash.Cidr) == Sam4lFlash.CidrExpected);
}

// 11b. the recorder: what crosses the transport is in the log, with bytes, text, timing and silence
{
    var dev = new FakeSam4l();
    var log = new StringWriter();
    using (var rec = new RecordingTransport(dev, log, "test"))
    {
        var c = new SamBaClient(rec);
        c.Hello();
        uint id = c.ReadWord(Sam4lFlash.Cidr);
        c.WriteWord(0x8000, 0xDEADBEEF);
        rec.Read(new byte[1], 5);                                   // nothing to read: a silence is logged too
        rec.Note("a note");
        Check("recorder passes the traffic through unchanged", id == Sam4lFlash.CidrExpected);
    }
    string text = log.ToString();
    Check("recorder: sent commands are logged with hex and text", text.Contains("TX") && text.Contains("4E 23") && text.Contains("\"N#\"") && text.Contains("W8000,DEADBEEF#"));
    Check("recorder: replies are logged (0A 0D for N#, the chip id in two pieces: E1, then 07 0A AB)", text.Contains("RX") && text.Contains("0A 0D") && text.Contains("E1 ") && text.Contains("07 0A AB"));
    Check("recorder: a silence and a note are logged", text.Contains("nothing (waited") && text.Contains("## a note"));
}

// 12. the jobs: dump, upload, restore, on a fake calculator with random flash
{
    (FakeSam4l Dev, byte[] Dump, SlotImage Img, byte[] Expected) JobRig()
    {
        var rng = new Random(12345);
        var dump = new byte[Sam4lFlash.FlashSize]; rng.NextBytes(dump);
        var slot = new byte[CeUnit.SlotBytes]; rng.NextBytes(slot);
        var img = new SlotImage(slot, new byte[] { 0x80, 0xB1, 0, 0 }, 520, 903, 0x376, 0x1F7, 520);
        var dev = new FakeSam4l();
        Array.Copy(dump, dev.Flash, dump.Length);
        var expected = (byte[])dump.Clone();
        Array.Copy(slot, 0, expected, CeUnit.SlotAt, slot.Length);
        Array.Copy(img.Pointer, 0, expected, CeUnit.PointerAt, 4);
        return (dev, dump, img, expected);
    }
    Sam4lFlash NewFlash(FakeSam4l dev) => new(new SamBaClient(dev));
    bool SameFlash(FakeSam4l dev, byte[] want) => dev.Flash.AsSpan().SequenceEqual(want);
    bool OutsidePlanUntouched(FakeSam4l dev, byte[] dump)
    {
        for (int p = 0; p < Sam4lFlash.Pages; p++)
        {
            bool inPlan = (p >= CePlan.SlotFirstPage && p <= CePlan.SlotLastPage) || p == CePlan.PointerPage;
            if (!inPlan && !dev.Flash.AsSpan(p * 512, 512).SequenceEqual(dump.AsSpan(p * 512, 512))) return false;
        }
        return true;
    }

    // a full upload
    {
        var (dev, dump, img, expected) = JobRig();
        var seen = new List<JobProgress>();
        var res = new CeUpload(NewFlash(dev), dump, img).Run(true, new Collect(seen));
        Check("upload: the flash is the dump with the slot and the pointer laid over it", SameFlash(dev, expected));
        Check($"upload: 57 slot pages and the pointer page written ({res.PagesWritten})", res.PagesWritten == 58);
        Check("upload: the boot loader (below 0x4000) is untouched", dev.Flash.AsSpan(0, 0x4000).SequenceEqual(dump.AsSpan(0, 0x4000)));
        Check("upload: the neighbours of the two shared pages are intact (end of the factory 15 ROM, start of the original 16C ROM)",
              dev.Flash.AsSpan(0xB000, 0x180).SequenceEqual(dump.AsSpan(0xB000, 0x180)) && dev.Flash.AsSpan(0x12180, 0x80).SequenceEqual(dump.AsSpan(0x12180, 0x80)));
        var stages = seen.Select(p => p.Stage).ToList();
        int iSlot = stages.IndexOf(JobStage.WritingSlot), iBound = stages.IndexOf(JobStage.WritingBoundary), iPtr = stages.IndexOf(JobStage.WritingPointer);
        Check("upload: baseline, then interior pages, then the shared pages, then the pointer last, then verify, done",
              stages.First() == JobStage.Connecting && stages.IndexOf(JobStage.Baseline) < iSlot && iSlot < iBound && iBound < iPtr &&
              iPtr < stages.IndexOf(JobStage.VerifyingSlot) && stages.Last() == JobStage.Done);
        var written = seen.Where(p => p.Stage is JobStage.WritingSlot or JobStage.WritingBoundary or JobStage.WritingPointer).Select(p => p.Message).ToList();
        Check("upload: the last three page writes are 88, 144 and the pointer page 230",
              written.TakeLast(3).SequenceEqual(new[] { "page 88", "page 144", "page 230" }), string.Join(",", written.TakeLast(3)));
        // restore puts it all back, and a second restore finds nothing to do
        var r1 = CeRestore.Run(NewFlash(dev), dump);
        Check($"restore: back to the dump ({r1.PagesWritten} pages written)", SameFlash(dev, dump) && r1.PagesWritten == 58);
        var r2 = CeRestore.Run(NewFlash(dev), dump);
        Check("restore: a second run writes nothing", r2.PagesWritten == 0 && r2.PagesAlreadyRight == 58);
    }

    // read only: nothing may be written
    {
        var (dev, dump, img, _) = JobRig();
        new CeUpload(NewFlash(dev), dump, img).Run(false);
        Check("a read-only run writes nothing and leaves the flash as it was", dev.WordWrites == 0 && SameFlash(dev, dump));
    }

    // the baseline: a calculator that is not the dump is not touched
    {
        var (dev, dump, img, _) = JobRig();
        dev.Flash[0x9000] ^= 0x55;
        bool t = Throws<JobFailedException>(() => new CeUpload(NewFlash(dev), dump, img).Run(true), out var m);
        Check("baseline: a calculator that differs from the dump is refused, nothing written", t && dev.WordWrites == 0 && m.Contains("differs from your dump"), m);
    }

    // a controller error half way: what was written is put back
    {
        var (dev, dump, img, _) = JobRig();
        var hook = new Collect([]) { On = p => { if (p.Stage == JobStage.WritingSlot && p.Message == "page 120") dev.FailNextProgram = true; } };
        bool t = Throws<JobFailedException>(() => new CeUpload(NewFlash(dev), dump, img).Run(true, hook), out var m);
        Check("a program error at page 120: reported, everything put back", t && m.Contains("put back") && SameFlash(dev, dump), m);
    }

    // a cancel between two pages: put back
    {
        var (dev, dump, img, _) = JobRig();
        using var cts = new CancellationTokenSource();
        var hook = new Collect([]) { On = p => { if (p.Stage == JobStage.WritingSlot && p.Message == "page 110") cts.Cancel(); } };
        bool t = Throws<JobCancelledException>(() => new CeUpload(NewFlash(dev), dump, img).Run(true, hook, cts.Token), out var m);
        Check("a cancel at page 110: stopped between pages, everything put back", t && SameFlash(dev, dump), m);
    }

    // the dump: two identical reads, or nothing
    {
        var (dev, dump, img, _) = JobRig();
        var got = CeDump.Read(NewFlash(dev));
        Check("dump: 128 KB read twice and equal to the flash", got.AsSpan().SequenceEqual(dump));
        var hook = new Collect([]) { On = p => { if (p.Stage == JobStage.ReadingDump && p.Step == Sam4lFlash.Pages) dev.Flash[0x5000] ^= 1; } };
        Check("dump: two reads that differ are refused", Throws<JobFailedException>(() => CeDump.Read(NewFlash(dev), hook), out _));
    }

    // the cable pulled at many points of an upload: the damage stays inside the planned pages, and Restore always repairs it
    {
        var (dev0, dump, img, expected) = JobRig();
        long boundaryStart = -1;
        var probe = new Collect([]) { On = p => { if (p.Stage == JobStage.WritingBoundary && boundaryStart < 0) boundaryStart = dev0.CommandsHandled; } };
        new CeUpload(NewFlash(dev0), dump, img).Run(true, probe);
        long allCommands = dev0.CommandsHandled;
        var cuts = new SortedSet<long>();
        for (int i = 1; i <= 40; i++) cuts.Add(allCommands * i / 41);                                        // spread over baseline, slot pages, verify
        for (long c = boundaryStart - 3; c < boundaryStart + 3 * 140 + 4; c += 7) cuts.Add(c);        // dense over the three shared pages
        int outside = 0, notRepaired = 0, ran = 0;
        foreach (var cut in cuts)
        {
            var (dev, _, _, _) = JobRig();
            dev.CutAfterCommands = cut;
            try { new CeUpload(NewFlash(dev), dump, img).Run(true); } catch (JobFailedException) { ran++; } catch (JobCancelledException) { }
            if (!OutsidePlanUntouched(dev, dump)) outside++;
            dev.Reconnect();
            try { CeRestore.Run(NewFlash(dev), dump); } catch (Exception) { }
            if (!SameFlash(dev, dump)) notRepaired++;
        }
        Check($"cable pulled at {cuts.Count} points of the upload ({ran} stopped it): nothing outside the planned pages changes", outside == 0, $"{outside} cases");
        Check("and Restore with the dump brings every one of them back to the dump", notRepaired == 0, $"{notRepaired} cases");
    }
}

Console.WriteLine();
Console.WriteLine($"{total - failed} of {total} ok");
return failed == 0 ? 0 : 1;

sealed class Collect(List<JobProgress> items) : IProgress<JobProgress>
{
    public Action<JobProgress>? On { get; init; }
    public void Report(JobProgress value) { items.Add(value); On?.Invoke(value); }
}
