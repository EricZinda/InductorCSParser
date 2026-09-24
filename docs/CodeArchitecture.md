# InductorParser C# Port Architecture

## Why Port

The Inductor Parser started life as a C++ template library written for Exospecies. It runs on Windows, macOS, and iOS and has been in production for years. The goal now is to get the same parser running inside a Unity game, including on WebGL, without losing what made the original nice to use: a small, readable, debuggable PEG parser where grammar rules read like the thing you're trying to parse.

Unity can host C++ as a native plugin on some targets, but not on WebGL (which compiles through Emscripten and has no native plugin story worth relying on) and not cleanly on iOS (where you'd be fighting the build system, App Store rules, and the IL2CPP linker). The path that actually works across every Unity target is to rewrite the parser in C# and ship it as a managed DLL.

This document is about the constraints that decision forces on us, and what the rewritten library has to look like to satisfy them.

## The Unity Constraint Stack

Unity imposes a cascade of constraints, each one narrowing what "C#" means. If the port ignores any of them, it'll work on desktop and fail on iOS or WebGL.

IL2CPP everywhere: Unity has [two scripting backends](https://docs.unity3d.com/Manual/scripting-backends.html): Mono (a small .NET VM shipped with the app that JIT-compiles IL at runtime) and IL2CPP (converts C# to C++ at build time and then compiles to native). iOS forbids runtime code generation (the [kernel prevents an app from generating code dynamically](https://learn.microsoft.com/en-us/previous-versions/xamarin/ios/internals/limitations), it's not just store policy), so Mono isn't an option there. WebGL runs in a browser as WebAssembly and has no VM to ship, so [it's an AOT platform too](https://docs.unity3d.com/Manual/webgl-technical-overview.html). Two of four targets are IL2CPP-only, so the library has to be written to survive IL2CPP on all targets. The cost is that reflection-heavy patterns, dynamic code generation, and anything that relies on the JIT producing code at runtime won't work (Unity's [scripting restrictions](https://docs.unity3d.com/Manual/scripting-restrictions.html) page has the full list).

WebGL is single-threaded: [Managed C# threads aren't supported on the Web platform](https://docs.unity3d.com/Manual/webgl-technical-overview.html) because WebAssembly has no multithreaded garbage collection for the scripting runtime to use. That means no `Task.Run`, `Thread`, `ThreadPool`, or blocking waits. A parser mostly doesn't care about this, because parsing is CPU-bound synchronous work, but it matters for anything async that touches I/O. The library has to avoid spawning threads internally and has to avoid any async primitive that schedules onto a thread pool.

No `System.Net` on WebGL: The browser sandbox has no socket access, so Unity [doesn't support any class in the `System.Net` namespace on the Web platform](https://docs.unity3d.com/Manual/webgl-networking.html). On iOS `System.Net.Http` exists but has a history of IL2CPP trouble (like [HttpClient hanging on Arm64 devices](https://issuetracker.unity3d.com/issues/il2cpp-android-ios-httpclient-content-stream-freezes-with-no-error-in-arm64-devices)), and Unity's answer on both platforms is `UnityWebRequest`. Not relevant for the parser itself, but it rules out any "helper" that wants to fetch grammars or include files over HTTP. Any I/O the library does has to be against an abstraction that the host can fill in.

No real file system on WebGL: The browser sandbox has no direct disk access. Emscripten gives the app [a virtual file system that lives in memory](https://emscripten.org/docs/porting/files/file_systems_overview.html), and anything that needs to persist [goes to the browser's IndexedDB](https://docs.unity3d.com/ScriptReference/Application-persistentDataPath.html). Also mostly not a parser problem, but the current C++ `Compiler` class knows how to load files from disk. That functionality has to move behind an interface that the host provides, or it has to go away. The core library can't call `System.IO.File.OpenRead` on the user's files and expect it to work on WebGL.

Memory limits: A 32-bit WebAssembly module can address at most 4 GB, and browsers [only started allowing the full 4 GB in 2020](https://v8.dev/blog/4gb-wasm-memory) (the cap was 2 GB before that). 64-bit WebAssembly [shipped in Chrome 133 and Firefox 134](https://spidermonkey.dev/blog/2025/01/15/is-memory64-actually-worth-using.html), but Unity builds 32-bit modules and caps the heap with the [Maximum Memory Size player setting](https://docs.unity3d.com/ScriptReference/PlayerSettings.WebGL-maximumMemorySize.html), which [defaults to 2 GB and can go up to 4 GB](https://docs.unity3d.com/Manual/webgl-memory.html). Mobile browsers hand out considerably less. The parser is unlikely to hit any of this, but it's a reminder that the port shouldn't hold references forever or build unbounded caches.

## Target Surface: .NET Standard 2.1

The library targets `netstandard2.1`: Unity's scripting runtime exposes .NET Standard 2.1 as its API compatibility level, and IL2CPP compiles against that surface area. A library targeting `net8.0` or `net6.0` would pull in APIs and runtime features IL2CPP doesn't provide, and it would either fail to load in Unity or fail at build time.

This is the single most important rule and it colors a lot of the smaller decisions. For example, .NET Standard 2.1 has `ReadOnlySpan<T>` and `Memory<T>`, so the lexer can work over spans instead of copying substrings around. It doesn't have `System.Text.Json` source generators, `[RequiresAssemblyFiles]`, C# 11 `required` members, or any of the .NET 7+ numeric abstractions. If a feature sounds new, assume it isn't available and check before using it.

Test projects are different: they target both `net8.0` and `net10.0` because they run under `dotnet test` and are never loaded by Unity. So, the library is `netstandard2.1` (plus a `net8.0` target for modern runtimes), while the same test suite runs on both supported CoreCLR versions.

## What the Port Can't Do

These are capabilities the C++ version has that the C# port will deliberately drop or push out to the host.

No file loading inside the core library: The C++ `Compiler::CompileDocument` takes a file path and opens it. The C# version can't do this on WebGL, so file loading becomes the host's job. The current core API takes a decoded `string` through `Rule.Parse`. The host is responsible for getting bytes off disk (or out of IndexedDB or off the network), decoding them, and handing the resulting text in.

No native debug logging hooks: The C++ version has `iOS/` and `Win/` platform directories for debug output. In C# that's replaced by a `TextWriter?` on `ParseOptions` that the host wires up to whatever logging it has. Unity can adapt this to `Debug.Log`, `dotnet test` can use `Console.Out` or a `StringWriter`, and the library doesn't need to know the difference.

No `FailFastAssert` that aborts the process. Aborting the process is fine in a game binary that owns its main function, but a library embedded in the Unity Editor can't take down the host. Internal assertions become `Invariant.That(...)` calls (in `src/InductorParser/Invariant.cs`) that throw `InductorParserBugException` when an internal invariant breaks. Bugs in the user's grammar throw `InvalidOperationException` with a message that points at the fix. Either way the host decides what happens instead of the process dying.

No threads spawned by the parser. The C++ version is already single-threaded for a typical parse, so this isn't a hard change, but it needs to stay that way. The port must not introduce any worker threads or background tasks inside the library.

## What the Port Has To Do

Translate C++ templates to something Unity-friendly: The C++ library uses templates extensively, and templates in C++ are a compile-time code generation mechanism. C# generics are a runtime mechanism with a different model, different capabilities, and different failure modes. Some patterns translate directly (a simple `AndExpression<T1, T2>` becomes `AndExpression<T1, T2>` with similar meaning). Other patterns don't. In particular, the C++ version uses a variadic-ish `Args` wrapper to work around the lack of variadic templates in the C++ version it was written for, and passes non-type template parameters like `FlattenType::Flatten` and numeric symbol IDs. C# generics don't support non-type generic parameters the way C++ templates do, and C# doesn't have variadic generics at all.

We need to preserve the tree shape and the flattening semantics. Whatever the grammar-authoring surface looks like, the resulting tree has to behave like the C++ one: custom IDs, `FlattenType::Flatten` / `Delete` / `None`, and `ToString()` recovering the original text. The one deliberate deviation is that `Parse` applies the flatten pass before returning, so the default `Tree` is the syntax tree. C++ callers did this explicitly via `FlattenInto`. Pass `ParseOptions.PreserveAllSymbols` for the C++-shaped raw tree when you need it. Downstream compilers written against the C++ version translate mechanically, just against the already-flattened tree.

Preserve the tracing story. The C++ version has very verbose parser tracing you can turn on with `SetTraceFilter(SystemTraceType::Parsing, TraceDetail::Diagnostic)`, and it's the main debugging tool for grammars. The C# port routes equivalent trace output through `ParseOptions.TraceSink`. This shouldn't use `System.Diagnostics.Trace` because that has IL2CPP baggage and is noisy on Unity.

Preserve the error reporting heuristic. The "deepest failure wins" heuristic is very useful. It has to come over intact.

## Assembly Layout

```
src/
  InductorParser/                          # netstandard2.1 class library (no Unity)
    InductorParser.csproj                  #   lexer, parser rules, syntax tree, tracing
  InductorParser.Tests/                    # net8.0 + net10.0, dotnet test only
    InductorParser.Tests.csproj
  InductorParser.ExternalContractTests/    # net8.0 + net10.0, dotnet test only
    InductorParser.ExternalContractTests.csproj
  Benchmarks/                              # net10.0 BenchmarkDotNet console app
    Benchmarks.csproj
```

One library, three consumers. `InductorParser.Tests` is the main test project. `InductorParser.ExternalContractTests` is a second test project that's deliberately left off the library's `InternalsVisibleTo` list: it recompiles the built-in rule sources against only the public and protected surface, so if a rule ever uses an internal member (or the extension surface narrows), that build breaks. `Benchmarks` is a BenchmarkDotNet console app that compares the parser against other .NET parsing libraries (Parlot, Pidgin, Sprache, Superpower, Pegasus) and regenerates the performance charts in the docs.

The C++ version has `FXPlatform` utilities (FailFast, NanoTrace, Utilities, Logger, etc.) that were shared with other Inductor projects. In C# most of those disappear: strings are strings, assertions are exceptions, logging is an interface. 

The built DLL is copied into the Unity project's `Assets/Plugins/` folder by a `CopyToUnity` msbuild target on the `.csproj`, following the UnityTabs convention. Unity picks up plugins in that folder automatically and makes them available to runtime and editor code.

```
Unity/
  Assets/
    Plugins/
      InductorParser.dll               # built from src/InductorParser
```

The library never references `UnityEngine` or `UnityEditor`. This is what makes `dotnet test` work against the same DLL that ships in Unity. If a line of code in the library needs something Unity-only, it's in the wrong layer and belongs in a host-supplied adapter.

## Async and Threading

The parser itself is synchronous. You hand it a string, it returns a tree. There's no `Task` in the public surface because there's nothing to await: parsing is CPU-bound. If a host wants to load a file asynchronously, it does so before calling into the library and hands in the string.

This is deliberate. Introducing `async Task<Symbol> ParseAsync(...)` in the core would force every caller to drag the `async` state machine through their code for no benefit. It also avoids any accidental use of `Task.Run` or `ConfigureAwait(false)` on WebGL, where the scheduler can't support them.

If future work needs async I/O (streaming a very large document, say), the shape will be: the host provides an `IReadableSource` abstraction that returns chunks synchronously, and the host handles async loading upstream. The parser stays oblivious.

## Dependency Injection

The library uses BCL types instead of bespoke interfaces for anything the host has to provide:

```csharp
public sealed class ParseOptions
{
    public TextWriter? TraceSink { get; set; }
    // ... other options
}
```

`TextWriter?` (set to `Console.Out`, a `StringWriter`, a file writer, or null for off) is the trace sink. There's no `ITraceSink` abstraction because every plausible sink is already a `TextWriter`, and the BCL type means callers can pipe trace output through anything that accepts text. File loading is similarly the host's job and not part of the library's surface: the host calls `File.ReadAllText(...)` (or whatever its environment supports) and hands the string to `Rule.Parse`.

The library doesn't depend on VContainer or any other DI framework. The current public surface is a static `Rules` class plus the `Rule`, `ParseOptions`, and `ParseResult` types, so there's no constructor to inject into. If a future revision adds a `Compiler<T>` base class, it would take its dependencies as plain constructor parameters, no container required.

## Performance

The C++ version's `readme.md` already warns that debug builds are dramatically slower than retail builds because of extra error checking. The C# port will have a similar story: debug-mode tracing and the Roslyn debug build both make parsing noticeably slower. 

One known risk: the C++ version leans on value types and stack allocation for a lot of its inner-loop state. C# will put more of that on the GC heap by default. The port should prefer `struct` for small, short-lived state objects (lexer transactions, position markers) and avoid allocating per-character. `Symbol` nodes are reference types and always will be, but the bookkeeping around them shouldn't allocate if it doesn't have to. Whether this matters in practice is a measurement question, not a design question. Start simple, profile, and tighten the hot paths that actually show up.

## What This Document Isn't

This isn't a line-by-line port plan. It doesn't decide which test framework to use, it doesn't decide on the final grammar-authoring syntax, and it doesn't enumerate every C++ file and its C# counterpart. Those decisions happen during the port itself, once we have one real grammar working end to end. The point of this document is to make the constraints explicit so none of those decisions accidentally paint us into a corner where the library runs on desktop and dies on WebGL.
