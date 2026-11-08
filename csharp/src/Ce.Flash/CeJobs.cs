using System.Diagnostics;

namespace Ce.Flash;

public enum JobStage { Connecting, ReadingDump, Baseline, WritingSlot, WritingBoundary, WritingPointer, VerifyingSlot, VerifyingPointer, Restoring, Comparing, Done }

/// <summary>An immutable snapshot for the screen: where the job is and how far.</summary>
public sealed record JobProgress(JobStage Stage, string Message, int Step, int Steps, TimeSpan Elapsed);

/// <summary>A job stopped. Restored: what had been written is back as it was. DeviceMayDiffer: run Restore with the dump.</summary>
public sealed class JobFailedException(string message, bool restored, bool deviceMayDiffer, Exception? inner = null) : Exception(message, inner)
{
    public bool Restored { get; } = restored;
    public bool DeviceMayDiffer { get; } = deviceMayDiffer;
}

/// <summary>The user cancelled between two pages; what had been written was put back.</summary>
public sealed class JobCancelledException(string message) : Exception(message);

/// <summary>The pages a slot upload (or its undo) touches, and what each must hold.</summary>
public static class CePlan
{
    public const int Page = Sam4lFlash.PageSize;
    /// <summary>Page 88: also holds the end of the factory 15 ROM (0xB000-0xB17F).</summary>
    public const int SlotFirstPage = CeUnit.SlotAt / Page;
    /// <summary>Page 144: also holds the start of the original 16C ROM (0x12180-).</summary>
    public const int SlotLastPage = (CeUnit.SlotAt + CeUnit.SlotBytes - 1) / Page;
    /// <summary>Page 230: firmware literals around the pointer.</summary>
    public const int PointerPage = CeUnit.PointerAt / Page;
    /// <summary>The boot loader below this is never compared with or written.</summary>
    public const int BaselineFrom = 0x4000;

    /// <summary>The pages the upload writes with their final content: the dump's page with the slot bytes (or the pointer) laid over it.</summary>
    public static List<(int Page, byte[] Data)> ForUpload(byte[] dump, SlotImage img)
    {
        var pages = new List<(int, byte[])>();
        for (int p = SlotFirstPage; p <= SlotLastPage; p++) pages.Add((p, Overlay(dump, p, CeUnit.SlotAt, img.Slot)));
        pages.Add((PointerPage, Overlay(dump, PointerPage, CeUnit.PointerAt, img.Pointer)));
        return pages;
    }

    /// <summary>The same pages as the dump has them: what Restore writes.</summary>
    public static List<(int Page, byte[] Data)> ForRestore(byte[] dump)
    {
        var pages = new List<(int, byte[])>();
        for (int p = SlotFirstPage; p <= SlotLastPage; p++) pages.Add((p, dump.AsSpan(p * Page, Page).ToArray()));
        pages.Add((PointerPage, dump.AsSpan(PointerPage * Page, Page).ToArray()));
        return pages;
    }

    static byte[] Overlay(byte[] dump, int page, int at, byte[] bytes)
    {
        var data = dump.AsSpan(page * Page, Page).ToArray();
        for (int i = 0; i < bytes.Length; i++)
        {
            int a = at + i - page * Page;
            if (a >= 0 && a < Page) data[a] = bytes[i];
        }
        return data;
    }

    /// <summary>The three pages that share bytes with other data.</summary>
    public static bool IsBoundary(int page) => page == SlotFirstPage || page == SlotLastPage || page == PointerPage;
}

/// <summary>Reads the whole flash twice and keeps it only when both reads are identical. Read only.</summary>
public static class CeDump
{
    public static byte[] Read(Sam4lFlash flash, IProgress<JobProgress>? progress = null, CancellationToken cancel = default)
    {
        var clock = Stopwatch.StartNew();
        var reads = new byte[2][];
        try
        {
            flash.Preflight();
            for (int pass = 0; pass < 2; pass++)
            {
                var bytes = new byte[Sam4lFlash.FlashSize];
                for (int p = 0; p < Sam4lFlash.Pages; p++)
                {
                    cancel.ThrowIfCancellationRequested();
                    flash.ReadPage(p).CopyTo(bytes, p * Sam4lFlash.PageSize);
                    progress?.Report(new JobProgress(JobStage.ReadingDump, $"read {pass + 1} of 2, page {p + 1} of {Sam4lFlash.Pages}",
                                                     pass * Sam4lFlash.Pages + p + 1, 2 * Sam4lFlash.Pages, clock.Elapsed));
                }
                reads[pass] = bytes;
            }
        }
        catch (IOException e) { throw new JobFailedException("the connection to the calculator was lost: nothing was written, nothing saved. Start over.", true, false, e); }
        catch (SamBaException e) { throw new JobFailedException(e.Message + ". Nothing was written.", true, false, e); }
        if (!reads[0].AsSpan().SequenceEqual(reads[1]))
            throw new JobFailedException("the two reads differ: nothing saved. Check the cable and start over.", true, false);
        return reads[0];
    }
}

/// <summary>
/// Writes the 16C+ image into the calculator: baseline check (the calculator equals the dump), the interior slot pages, then
/// the three pages that share bytes with other data (first and last slot page, pointer page) back to back, the pointer last,
/// then a read-back of the slot and the pointer. What was written is saved page by page and put back when anything fails.
/// A cancel between two pages puts back too. Nothing is written unless <c>write</c> is true and the baseline passed in this run.
/// </summary>
public sealed class CeUpload(Sam4lFlash flash, byte[] dump, SlotImage image, Action<int, byte[]>? savePreImage = null)
{
    public sealed record Result(int PagesWritten, int PagesAlreadyRight, TimeSpan Elapsed);

    public Result Run(bool write, IProgress<JobProgress>? progress = null, CancellationToken cancel = default)
    {
        var clock = Stopwatch.StartNew();
        void Say(JobStage s, string m, int step, int steps) => progress?.Report(new JobProgress(s, m, step, steps, clock.Elapsed));
        Say(JobStage.Connecting, "checking the chip", 0, 1);
        try
        {
            flash.Preflight();
            CeRestore.CompareToDump(flash, dump, JobStage.Baseline, "baseline: the calculator equals your dump", progress, clock, cancel);
        }
        catch (IOException e) { throw new JobFailedException("the connection to the calculator was lost before anything was written. Nothing changed; start over.", true, false, e); }
        catch (SamBaException e) { throw new JobFailedException(e.Message + ". Nothing was written.", true, false, e); }
        if (!write) return new Result(0, 0, clock.Elapsed);

        var plan = CePlan.ForUpload(dump, image);
        var interior = plan.Where(p => !CePlan.IsBoundary(p.Page)).ToList();
        var boundary = plan.Where(p => CePlan.IsBoundary(p.Page)).OrderBy(p => p.Page == CePlan.PointerPage ? 1 : 0).ThenBy(p => p.Page).ToList();
        var undoLog = new List<(int Page, byte[] Pre)>();
        int written = 0, right = 0, total = plan.Count;
        try
        {
            foreach (var (page, data) in interior)
            {
                if (cancel.IsCancellationRequested) throw new OperationCanceledException();
                WriteOne(page, data, JobStage.WritingSlot);
            }
            foreach (var (page, data) in boundary)               // back to back, no cancel: the exposure is a few milliseconds each
                WriteOne(page, data, page == CePlan.PointerPage ? JobStage.WritingPointer : JobStage.WritingBoundary);

            Say(JobStage.VerifyingSlot, "reading the slot back", 0, 1);
            if (!flash.ReadRange(CeUnit.SlotAt, CeUnit.SlotBytes).AsSpan().SequenceEqual(image.Slot))
                throw new SamBaException("the slot does not read back as the image");
            Say(JobStage.VerifyingPointer, "reading the pointer back", 0, 1);
            if (!flash.ReadRange(CeUnit.PointerAt, 4).AsSpan().SequenceEqual(image.Pointer))
                throw new SamBaException("the pointer does not read back");
        }
        catch (OperationCanceledException)
        {
            Undo(undoLog, progress, clock);
            throw new JobCancelledException("cancelled: what had been written was put back");
        }
        catch (IOException e)                                    // the link is gone: nothing can be written from here
        {
            throw new JobFailedException("the connection to the calculator was lost. Put it into programming mode again and run Restore with your dump.", false, true, e);
        }
        catch (SamBaException e)
        {
            bool ok = Undo(undoLog, progress, clock);
            throw new JobFailedException(ok ? $"{e.Message}. What had been written was put back."
                                            : $"{e.Message}. Putting back failed too: run Restore with your dump.", ok, !ok, e);
        }
        Say(JobStage.Done, "done", total, total);
        return new Result(written, right, clock.Elapsed);

        void WriteOne(int page, byte[] data, JobStage stage)
        {
            Say(stage, $"page {page}", written + right + 1, total);
            var w = flash.WritePageChecked(page, data);
            if (w.Changed) { undoLog.Add((page, w.PreImage)); savePreImage?.Invoke(page, w.PreImage); written++; } else right++;
        }
    }

    bool Undo(List<(int Page, byte[] Pre)> undoLog, IProgress<JobProgress>? progress, Stopwatch clock)
    {
        try
        {
            for (int i = undoLog.Count - 1; i >= 0; i--)
            {
                progress?.Report(new JobProgress(JobStage.Restoring, $"putting page {undoLog[i].Page} back", undoLog.Count - i, undoLog.Count, clock.Elapsed));
                flash.WritePageChecked(undoLog[i].Page, undoLog[i].Pre);
            }
            return true;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>Puts the pages of the slot and the pointer back as the dump has them, then compares the whole flash with the dump (read only).</summary>
public static class CeRestore
{
    public static CeUpload.Result Run(Sam4lFlash flash, byte[] dump, IProgress<JobProgress>? progress = null, CancellationToken cancel = default, bool compareAll = true)
    {
        var clock = Stopwatch.StartNew();
        progress?.Report(new JobProgress(JobStage.Connecting, "checking the chip", 0, 1, clock.Elapsed));
        try { flash.Preflight(); }
        catch (IOException e) { throw new JobFailedException("the connection to the calculator was lost: put it into programming mode again and run Restore again.", false, true, e); }
        catch (SamBaException e) { throw new JobFailedException(e.Message, false, true, e); }
        var plan = CePlan.ForRestore(dump);
        int written = 0, right = 0;
        // the pointer first: the 16 mode runs the original ROM again before the slot is touched; the pages that share data last
        foreach (var (page, data) in plan.OrderBy(p => p.Page == CePlan.PointerPage ? 0 : CePlan.IsBoundary(p.Page) ? 2 : 1).ThenBy(p => p.Page))
        {
            if (cancel.IsCancellationRequested && !CePlan.IsBoundary(page))
                throw new JobCancelledException("cancelled: the calculator is part way back; run Restore again");
            progress?.Report(new JobProgress(JobStage.Restoring, $"page {page}", written + right + 1, plan.Count, clock.Elapsed));
            try
            {
                if (flash.WritePageChecked(page, data).Changed) written++; else right++;
            }
            catch (IOException e) { throw new JobFailedException("the connection to the calculator was lost: put it into programming mode again and run Restore again.", false, true, e); }
            catch (SamBaException e) { throw new JobFailedException(e.Message, false, true, e); }
        }
        if (compareAll)
        {
            try { CompareToDump(flash, dump, JobStage.Comparing, "the whole flash equals your dump", progress, clock, cancel); }
            catch (IOException e) { throw new JobFailedException("the connection was lost while comparing; the slot and the pointer are back. Run the comparison again.", true, false, e); }
        }
        progress?.Report(new JobProgress(JobStage.Done, "done", plan.Count, plan.Count, clock.Elapsed));
        return new CeUpload.Result(written, right, clock.Elapsed);
    }

    /// <summary>Read the calculator from 0x4000 and compare with the dump; throws at the first differing page.</summary>
    internal static void CompareToDump(Sam4lFlash flash, byte[] dump, JobStage stage, string text, IProgress<JobProgress>? progress,
                                       Stopwatch clock, CancellationToken cancel)
    {
        int first = CePlan.BaselineFrom / Sam4lFlash.PageSize, last = Sam4lFlash.Pages;
        for (int p = first; p < last; p++)
        {
            cancel.ThrowIfCancellationRequested();
            var now = flash.ReadPage(p);
            if (!now.AsSpan().SequenceEqual(dump.AsSpan(p * Sam4lFlash.PageSize, Sam4lFlash.PageSize)))
            {
                int at = Enumerable.Range(0, Sam4lFlash.PageSize).First(i => now[i] != dump[p * Sam4lFlash.PageSize + i]);
                throw new JobFailedException($"the calculator differs from your dump at {p * Sam4lFlash.PageSize + at:X5} (page {p}). " +
                                             "If you used the calculator since the dump, take a new dump.", true, false);
            }
            progress?.Report(new JobProgress(stage, $"{text}: page {p + 1 - first} of {last - first}", p + 1 - first, last - first, clock.Elapsed));
        }
    }
}
