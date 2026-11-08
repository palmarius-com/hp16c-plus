namespace Ce.Flash;

/// <summary>What a checked page write did.</summary>
/// <param name="Changed">false: the page already held the data and was not touched.</param>
/// <param name="PreImage">the page as it was before (for the log and the undo).</param>
public sealed record PageWrite(bool Changed, byte[] PreImage);

/// <summary>
/// The flash controller of the ATSAM4L in the 15C CE (128 KB, 512-byte pages, 16 regions of 8 KB), through the monitor.
/// Rules that cannot be switched off: the only words written to the controller are FCMD with key 0xA5 and WP (1), EP (2) or
/// CPB (3) for the current page; the only other words written are the 128 words of that page; nothing below
/// <see cref="MinWriteAddress"/> is ever written (the boot loader); a write is read back and, when it does not read
/// back, the pre-image is written back.
/// </summary>
public sealed class Sam4lFlash
{
    public const int PageSize = 512;
    public const int FlashSize = 0x20000;
    public const int Pages = FlashSize / PageSize;
    public const uint Fcmd = 0x400A0004, Fsr = 0x400A0008, Cidr = 0x400E0740;
    public const uint CidrExpected = 0xAB0A07E1;
    public const uint ErrorBits = 0xC;                      // PROGE | LOCKE
    const int CmdWp = 1, CmdEp = 2, CmdCpb = 3;

    readonly SamBaClient _c;
    readonly uint _minWrite;
    int _page = -1;

    /// <param name="minWriteAddress">Nothing below this is written. 0x4000 for the CE; tests lower it to reach the device's own protection.</param>
    public Sam4lFlash(SamBaClient client, uint minWriteAddress = 0x4000)
    {
        _c = client;
        _minWrite = minWriteAddress;
    }

    public uint MinWriteAddress => _minWrite;
    public SamBaClient Client => _c;

    /// <summary>Hello, chip id, controller ready and free of errors. Throws when this is not the chip the kit knows.</summary>
    public void Preflight()
    {
        _c.Hello();
        uint id = _c.ReadWord(Cidr);
        if (id != CidrExpected) throw new SamBaException($"unknown chip id {id:X8} (expected {CidrExpected:X8}): nothing was written");
        uint f = ReadFsr();
        if ((f & 1) == 0) throw new SamBaException($"flash controller not ready, FSR {f:X8}");
        if ((f & ErrorBits) != 0) throw new SamBaException($"flash controller reports an error, FSR {f:X8}");
    }

    public uint ReadFsr() => _c.ReadWord(Fsr);

    public byte[] ReadPage(int page) => ReadRange((uint)(page * PageSize), PageSize);

    /// <summary>Read length bytes (a multiple of 4) from flash address start.</summary>
    public byte[] ReadRange(uint start, int length, Action<int>? progress = null)
    {
        if (length % 4 != 0 || start % 4 != 0) throw new ArgumentException("word aligned reads only");
        var bytes = new byte[length];
        for (int k = 0; k < length / 4; k++)
        {
            BitConverter.GetBytes(_c.ReadWord(start + (uint)(4 * k))).CopyTo(bytes, 4 * k);
            if (progress is not null && (k & 0x7F) == 0x7F) progress(4 * (k + 1));
        }
        return bytes;
    }

    /// <summary>Erase the page and write it: clear buffer, erase, clear buffer, 128 words, write. Not checked afterwards.</summary>
    public void ProgramPage(int page, ReadOnlySpan<byte> data)
    {
        if (data.Length != PageSize) throw new ArgumentException($"a page is {PageSize} bytes");
        if (page < 0 || page >= Pages) throw new SamBaException($"page {page} does not exist");
        if ((uint)(page * PageSize) < _minWrite)
            throw new SamBaException($"refused: page {page} (0x{page * PageSize:X}) is below 0x{_minWrite:X}");
        _page = page;
        _ = ReadFsr();                                      // a read clears PROGE / LOCKE
        Command(CmdCpb, page); WaitReady();
        Command(CmdEp, page);
        uint f = WaitReady();
        if ((f & ErrorBits) != 0) throw new SamBaException($"erase error, FSR {f:X8}");
        Command(CmdCpb, page); WaitReady();
        for (int k = 0; k < PageSize / 4; k++)              // low word first, ascending
            WriteChecked((uint)(page * PageSize + 4 * k), BitConverter.ToUInt32(data[(4 * k)..]));
        Command(CmdWp, page);
        f = WaitReady();
        if ((f & ErrorBits) != 0) throw new SamBaException($"write error, FSR {f:X8}");
    }

    /// <summary>
    /// Write a page and read it back. When it does not read back (or the controller reports an error) the pre-image is
    /// written back and an exception says so. A lost link (IOException) cannot be repaired from here and passes through.
    /// </summary>
    public PageWrite WritePageChecked(int page, byte[] data)
    {
        var pre = ReadPage(page);
        if (pre.AsSpan().SequenceEqual(data)) return new PageWrite(false, pre);
        string why;
        try
        {
            ProgramPage(page, data);
            if (ReadPage(page).AsSpan().SequenceEqual(data)) return new PageWrite(true, pre);
            why = "the page did not read back as written";
        }
        catch (SamBaException e) { why = e.Message; }
        ProgramPage(page, pre);
        if (!ReadPage(page).AsSpan().SequenceEqual(pre)) throw new SamBaException($"page {page}: {why}; restoring the saved pre-image failed too");
        throw new SamBaException($"page {page}: {why}; the saved pre-image was written back");
    }

    void Command(int cmd, int page) =>
        WriteChecked(Fcmd, (uint)((0xA5u << 24) | ((uint)page << 8) | (uint)cmd));

    uint WaitReady()
    {
        for (int i = 0; i < 60; i++)
        {
            uint f = ReadFsr();
            if ((f & 1) != 0) return f;
            Thread.Sleep(50);
        }
        throw new SamBaException("flash controller never became ready");
    }

    /// <summary>The only way a word leaves this class: FCMD for the current page, or a word inside the current page.</summary>
    void WriteChecked(uint address, uint value)
    {
        if (address == Fcmd)
        {
            uint key = value >> 24, cmd = value & 0x3F, pg = (value >> 8) & 0xFFFF;
            if (key != 0xA5) throw new SamBaException("FCMD key");
            if (cmd is not (CmdWp or CmdEp or CmdCpb)) throw new SamBaException($"refused FCMD command {cmd} (only WP, EP and CPB exist here)");
            if (pg != _page) throw new SamBaException($"FCMD page {pg} is not the current page {_page}");
        }
        else
        {
            uint lo = (uint)(_page * PageSize), hi = lo + PageSize - 4;
            if (_page < 0 || lo < _minWrite || address < lo || address > hi || address % 4 != 0)
                throw new SamBaException($"refused write to {address:X8}");
        }
        _c.WriteWord(address, value);
    }
}
