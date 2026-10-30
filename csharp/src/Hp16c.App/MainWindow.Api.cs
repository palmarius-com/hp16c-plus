using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Nut.Core;

namespace HpSim;

/// <summary>Commands of the --api channel (see docs/API.md).</summary>
sealed partial class MainWindow
{
    async Task<string> ApiCommand(string line)
    {
        var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "error empty command";
        var arg = parts.Length > 1 ? parts[1].Trim() : "";
        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "start":
                    if (!StartByName(arg)) return $"error no calculator '{arg}'";
                    return _runner is null ? $"error not started: {_message.Text}" : $"ok {_session!.Model.Id}";
                case "patch":
                    if (_session is null) return "error no calculator running";
                    _session.Patches.Add((Path.GetFileName(arg), RomImage.Load(arg)));
                    Run(_session);
                    return $"ok {_session.Patches.Count} patches";
                case "unpatch":
                    RemovePatches();
                    return "ok";
                case "download":
                    await RomStore.Download(ModelCatalog.Get(arg));
                    return "ok " + RomStore.Relative(RomStore.OriginalFile(arg)!);
                case "testrun":
                    var failures = new List<string>();
                    var sum = await RunTests("", await File.ReadAllLinesAsync(arg), CancellationToken.None,
                        r => { if (!r.Ok) lock (failures) failures.Add($"line {r.Line}: {r.Text} {r.Detail}"); });
                    if (failures.Count > 0) await File.WriteAllLinesAsync(arg + ".failures", failures);
                    return "ok " + sum + (failures.Count > 0 ? " | " + string.Join(" | ", failures.Take(5)) : "");
                case "chips":
                    if (_session is null) return "error no calculator running";
                    _session.UserLarge = arg switch { "own" => false, "large" => true, "preset" => null,
                        _ => throw new ArgumentException("chips own, large or preset") };
                    Run(_session);
                    return "ok " + _message.Text;
                case "label":
                    if (arg.Length > 0) SetLabel(arg == "auto" ? null : arg);
                    return "ok " + _calc.Skin.Label;
                case "message":
                    return "ok " + _message.Text;
                case "skin":
                    if (arg == "auto") SetSkin(null, null);
                    else if (arg.Length > 0) SetSkin(await File.ReadAllTextAsync(arg), Path.GetFileName(arg));
                    return "ok " + (_session?.SkinName ?? "automatic");
                case "skin-save":
                    await File.WriteAllTextAsync(arg, _calc.Skin.ToJson());
                    return "ok";
                case "changes":
                    return "ok " + string.Join("; ", _session?.Changes() ?? []);
                case "save-my":
                case "save-over":
                    if (_session is null) return "error no calculator running";
                    var words = arg.Split(' ', 2);
                    if (RomStore.ShortNameProblem(words[0]) is { } issue) return "error " + issue;
                    if (parts[0].Equals("save-my", StringComparison.OrdinalIgnoreCase) && RomStore.ShortNameTaken(words[0]))
                        return $"error '{words[0]}' already exists; save-over replaces it";
                    var saved = RomStore.SaveMyCalc(words[0], words.Length > 1 ? words[1] : words[0], _session.Model.Id, _calc.Skin.Label, "",
                                                    _session.Image(), _session.UserLarge,
                                                    _session.SkinJson, _session.SkinName);
                    _session.MarkSaved();
                    RefreshMyMenu();
                    return "ok " + saved.Id;
                case "delete-my":
                    var mine = RomStore.MyCalcs().FirstOrDefault(c => string.Equals(c.Name, arg, StringComparison.OrdinalIgnoreCase) ||
                                                                 string.Equals(c.Id, arg, StringComparison.OrdinalIgnoreCase));
                    if (mine is null) return $"error no calculator '{arg}'";
                    RomStore.DeleteMyCalc(mine);
                    RefreshMyMenu();
                    return "ok";
                case "menu":
                    return "ok " + string.Join("; ", _calcItems.OfType<MenuItem>().Select(m => m.Header?.ToString()));
                case "rominfo":
                    return _session is null ? "error no calculator running" : "ok " + _session.Info();
                case "down":
                    _calc.PressFromKeyboard(int.Parse(arg));
                    return "ok";
                case "up":
                    _calc.ReleaseFromKeyboard();
                    return "ok";
                case "chord":
                    if (_runner is null) return "error no calculator running";
                    var pair = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    await _calc.PlayChord(int.Parse(pair[0]), int.Parse(pair[1]));
                    return "ok";
                case "poll":
                    if (_runner is null) return "error no calculator running";
                    return $"ok {(_runner.Finished ? 1 : 0)} {(_runner.KeysApplied ? 1 : 0)} " +
                           $"{(_runner.Read(v => v.Cpu.Awake) ? 1 : 0)} {(_runner.Read(v => v.DisplayEnable) ? 1 : 0)} " +
                           $"{_runner.Read(v => v.Cpu.Cycles)}";
                case "display":
                    return "ok " + (_runner?.Read(v => v.Display()) ?? "");
                case "flags":
                    var ann = _runner?.Read(v => v.Annunciators()) ?? [];
                    return "ok " + string.Join(" ", ann.Select(a => _calc.Skin.Annunciators[a]));
                case "panelview":
                    _view.SelectedIndex = arg switch { "w" => 0, "64" => 1, "128" => 2,
                        _ => throw new ArgumentException("panelview w, 64 or 128") };
                    return "ok";
                case "status":
                    return "ok " + (_runner?.Take().Status ?? "");
                case "save-state":
                    if (_runner is null) return "error no calculator running";
                    await File.WriteAllTextAsync(arg, CurrentState().ToJson());
                    return "ok";
                case "load-state":
                    return ApplyState(MachineState.FromJson(await File.ReadAllTextAsync(arg)), false);
                case "load-state-anyway":
                    return ApplyState(MachineState.FromJson(await File.ReadAllTextAsync(arg)), true);
                case "export-patch":
                    if (PatchText() is not { } patchText) return "error nothing to export";
                    await File.WriteAllTextAsync(arg, patchText);
                    return "ok";
                case "export-rom":
                    if (_session is null) return "error no calculator running";
                    await File.WriteAllTextAsync(arg, _session.Image().ToNonpareilText("flow test"));
                    return "ok";
                case "screenshot":
                    Tick();
                    await Task.Delay(100);
                    var size = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
                    using (var bmp = new RenderTargetBitmap(size, new Vector(96, 96)))
                    {
                        bmp.Render(this);
                        bmp.Save(arg);
                    }
                    return "ok";
                case "quit":
                    _closeNow = true;
                    Close();
                    return "ok";
                default:
                    return $"error unknown command '{parts[0]}'";
            }
        }
        catch (Exception ex) { return "error " + ex.Message; }
    }
}
