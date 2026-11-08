# Building the simulator from source

For an intermediate version between two releases: the `main` branch carries the work for the next release. Its version number is in
`src/Hp16c.App/Hp16c.App.csproj` (`<Version>`, shown in the window title). Only Windows is released and tested; the source builds on macOS and
Linux too (Avalonia), untested.

## What you need

- Visual Studio Code (https://code.visualstudio.com) with the C# Dev Kit extension (ms-dotnettools.csdevkit). Open the `csharp` folder in it; the
  terminal inside VS Code runs the commands below. A plain terminal does the same.
- The .NET 10 SDK (not only the Runtime): https://dotnet.microsoft.com/download. `dotnet --version` must print 10.x. VS Code finds it by itself.
- Git, to clone the repository.
- Internet on the first build: NuGet fetches Avalonia and the other packages (the list is in `nuget.config`; nothing else is downloaded).

No ROM is in the repository. The simulator downloads the Nonpareil ROMs on request (ROM menu, one click) or loads a ROM file you give it.

## Build and run

```sh
git clone https://github.com/palmarius-com/hp16c-plus.git
cd hp16c-plus/csharp
dotnet run --project src/Hp16c.App                    # build and start the simulator
dotnet run --project src/Hp16c.App -- --model 15c     # start a model directly (10c, 11c, 12c, 15c, 16c)
```

## One `VoyagerSim.exe` for Windows

```sh
cd hp16c-plus/csharp
dotnet publish src/Hp16c.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish/win-x64
```

The result is `publish/win-x64/VoyagerSim.exe`. It needs the .NET 10 Runtime on the machine where it runs. Put the `docs/examples` folder next to it
(the Test window opens its dialog in a folder called `examples` beside the exe).

## The tests

```sh
dotnet run --project tests/Nut.Replay -c Release      # the Nut core against stored cases, about 1 s
```

The test files of the examples run in the simulator itself (Help > Test window > Open..., see `docs/TESTS.md`), each on its own calculator.

## Debugging a ROM in VS Code

VS Code attaches to the running simulator (started with `--debug-port 4711`) and shows the ROM, registers and RAM, with breakpoints and single steps.
Nothing is copied: the calculator in the window is the one you debug. How to set it up: `docs/DEBUGGER.md`; the small VS Code extension that declares the
debugger type `nut` (and the `.nutasm` listing) is in `tools/vscode-nut`.

## 16C+

The 16C+ is a patch for the 16C ROM, not a program: `docs/examples/16cplus.patch`. Start the 16C, ROM > Apply a patch..., then
Calculator > Save as my calculator. See `docs/PATCHING.md`.

## What the intermediate version has over the last release

See the commits of `main` since the last release tag. The first thing after 0.5.5: the self test of the 15C (`ON` and `×`) passes
(second chip, `src/Nut.Core/Voyager.cs`), and `docs/examples/hp15c-le-known-bugs-tests.txt` was added.
