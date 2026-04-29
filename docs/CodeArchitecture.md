# InductorParser C# Port Architecture

## Why Port

The Inductor Parser started life as a C++ template library written for Exospecies. It runs on Windows, macOS, and iOS and has been in production for years. The goal now is to get the same parser running inside a Unity game, including on WebGL, without losing what made the original nice to use: a small, readable, debuggable PEG parser where grammar rules read like the thing you are trying to parse.

Unity can host C++ as a native plugin on some targets, but not on WebGL (which compiles through Emscripten and has no native plugin story worth relying on) and not cleanly on iOS (where you would be fighting the build system, App Store rules, and the IL2CPP linker). The path that actually works across every Unity target is to rewrite the parser in C# and ship it as a managed DLL.

This document is about the constraints that decision forces on us, and what the rewritten library has to look like to satisfy them.

## The Unity Constraint Stack

Unity imposes a cascade of constraints, each one narrowing what "C#" means. If the port ignores any of them, it will work on desktop and fail on iOS or WebGL.

IL2CPP everywhere. Unity has two scripting backends: Mono (a small .NET VM shipped with the app, JIT-compiles IL at runtime) and IL2CPP (converts C# to C++ at build time, then compiles to native). iOS forbids runtime JIT by Apple policy, so Mono is not an option there. WebGL runs in a browser as WebAssembly and has no VM to ship, so it also needs ahead-of-time compilation. Two of four targets are IL2CPP-only, so the library has to be written to survive IL2CPP on all targets. The cost is that reflection-heavy patterns, dynamic code generation, and anything that relies on the JIT producing code at runtime simply will not work.

WebGL is single-threaded. Browsers run JavaScript (and therefore WebAssembly) on one thread. That means no `Task.Run`, no `Thread`, no `ThreadPool`, no blocking waits. A parser mostly does not care about this, because parsing is CPU-bound synchronous work, but it matters for anything async that touches I/O. The library has to avoid spawning threads internally and has to avoid any async primitive that schedules onto a thread pool.

No `System.Net.Http` on iOS or WebGL. Not relevant for the parser itself, but it rules out any "helper" that wants to fetch grammars or include files over HTTP. Any I/O the library does has to be against an abstraction that the host can fill in.

No real file system on WebGL. Also mostly not a parser problem, but the current C++ `Compiler` class knows how to load files from disk. That functionality has to move behind an interface that the host provides, or it has to go away. The core library cannot call `System.IO.File.OpenRead` and expect it to work on WebGL.

Memory limits. Browsers cap WebAssembly memory (typically 2 GB). The parser is unlikely to hit this, but it is a reminder that the port should not hold references forever or build unbounded caches.

## Target Surface: .NET Standard 2.1

The library targets `netstandard2.1`. Unity's scripting runtime exposes .NET Standard 2.1 as its API compatibility level, and IL2CPP compiles against that surface area. A library targeting `net8.0` or `net6.0` would pull in APIs and runtime features IL2CPP does not provide, and it would either fail to load in Unity or fail at build time.

This is the single most important rule and it colors a lot of the smaller decisions. For example, .NET Standard 2.1 has `ReadOnlySpan<T>` and `Memory<T>`, so the lexer can work over spans instead of copying substrings around. It does not have `System.Text.Json` source generators, `[RequiresAssemblyFiles]`, C# 11 `required` members, or any of the .NET 7+ numeric abstractions. If a feature sounds new and shiny, assume it is not available and check before using it.

Test projects are different. `InductorParser.Tests` can target `net8.0` because it only runs under `dotnet test` and is never loaded by Unity. That is the standard split: library is `netstandard2.1`, tests are `net8.0`.

## What the Port Cannot Do

These are capabilities the C++ version has that the C# port will deliberately drop or push out to the host.

No file loading inside the core library. The C++ `Compiler::CompileDocument` takes a file path and opens it. The C# version cannot do this on WebGL, so file loading becomes the host's job. The `Compiler` class will take a string or a `Stream` or a `TextReader`, and the host is responsible for getting bytes off disk (or out of IndexedDB, or off the network) and handing them in.

No native debug logging hooks. The C++ version has `iOS/` and `Win/` platform directories for debug output. In C# that is replaced by a single `ITraceSink` interface (or just an `Action<string>` callback) that the host wires up to whatever logging it has. Unity has `Debug.Log`, `dotnet test` has `Console.Out`, and the library does not need to know the difference.

No `FailFastAssert` that aborts the process. Aborting the process is fine in a game binary that owns its main function, but a library embedded in the Unity Editor cannot take down the host. Assertions become exceptions. The library throws on contract violations and lets the host decide what to do.

No threads spawned by the parser. The C++ version is already single-threaded for a typical parse, so this is not a hard change, but it needs to stay that way. The port must not introduce any worker threads or background tasks inside the library.

## What the Port Has To Do

Translate C++ templates to something Unity-friendly: The C++ library uses templates extensively, and templates in C++ are a compile-time code generation mechanism. C# generics are a runtime mechanism with a different model, different capabilities, and different failure modes. Some patterns translate directly (a simple `AndExpression<T1, T2>` becomes `AndExpression<T1, T2>` with similar meaning). Other patterns do not. In particular, the C++ version uses a variadic-ish `Args` wrapper to work around the lack of variadic templates in the C++ version it was written for, and passes non-type template parameters like `FlattenType::Flatten` and numeric symbol IDs. C# generics do not support non-type generic parameters the way C++ templates do, and C# does not have variadic generics at all.

We need to preserve the tree shape and the flattening semantics. Whatever the grammar-authoring surface looks like, the resulting tree has to behave like the C++ one: custom IDs, `FlattenType::Flatten` / `Delete` / `None`, and `ToString()` recovering the original text. The one deliberate deviation is that `Parse` applies the flatten pass before returning, so the default `Tree` is the syntax tree. C++ callers did this explicitly via `FlattenInto`. Pass `ParseOptions.PreserveAllSymbols` for the C++-shaped raw tree when you need it. Downstream compilers written against the C++ version translate mechanically, just against the already-flattened tree.

Preserve the tracing story. The C++ version has very verbose parser tracing you can turn on with `SetTraceFilter(SystemTraceType::Parsing, TraceDetail::Diagnostic)`, and it is the main debugging tool for grammars. The C# port needs an equivalent, routed through whatever `ITraceSink` the host provides. This should not use `System.Diagnostics.Trace` because that has IL2CPP baggage and is noisy on Unity.

Preserve the error reporting heuristic. The "deepest failure wins" heuristic is very useful. It has to come over intact.

## Assembly Layout

```
src/
  InductorParser/                      # netstandard2.1 class library (no Unity)
    InductorParser.csproj              #   lexer, parser rules, compiler, tracing
  InductorParser.Tests/                # net8.0, dotnet test only
    InductorParser.Tests.csproj
```

One library, one test project. The C++ version has `FXPlatform` utilities (FailFast, NanoTrace, Utilities, Logger, etc.) that were shared with other Inductor projects. In C# most of those disappear: strings are strings, assertions are exceptions, logging is an interface. Anything that genuinely needs to be shared with a hypothetical C# port of InductorProlog lives in its own assembly later, not on day one.

The built DLL is copied into the Unity project's `Assets/Plugins/` folder by a `CopyToUnity` msbuild target on the `.csproj`, following the UnityTabs convention. Unity picks up plugins in that folder automatically and makes them available to runtime and editor code.

```
Unity/
  Assets/
    Plugins/
      InductorParser.dll               # built from src/InductorParser
```

The library never references `UnityEngine` or `UnityEditor`. This is what makes `dotnet test` work against the same DLL that ships in Unity. If a line of code in the library needs something Unity-only, it is in the wrong layer and belongs in a host-supplied adapter.

## Async and Threading

The parser itself is synchronous. You hand it a string, it returns a tree. There is no `Task` in the public surface because there is nothing to await: parsing is CPU-bound. The compiler layer, which in the C++ version reads files from disk, also stays synchronous in its core API. If a host wants to load a file asynchronously, it does so before calling into the library and hands in the string.

This is deliberate. Introducing `async Task<Symbol> ParseAsync(...)` in the core would force every caller to drag the `async` state machine through their code for no benefit. It also avoids any accidental use of `Task.Run` or `ConfigureAwait(false)` on WebGL, where the scheduler cannot support them.

If future work needs async I/O (streaming a very large document, say), the shape will be: the host provides an `IReadableSource` abstraction that returns chunks synchronously, and the host handles async loading upstream. The parser stays oblivious.

## Dependency Injection

The library exposes interfaces for anything the host has to provide:

```csharp
public interface ITraceSink { void Write(string category, string message); }
public interface IReadableSource { string ReadToEnd(); }   // if needed
```

These are constructor-injected into the `Compiler` and `Lexer`. The library does not depend on VContainer or any other DI framework. Tests wire them up by hand. Unity hosts wire them up through VContainer or through a simple static registration, whichever the host prefers. The library doesn't care.

## Performance

The C++ version's `readme.md` already warns that debug builds are dramatically slower than retail builds because of extra error checking. The C# port will have a similar story: debug-mode tracing and the Roslyn debug build both make parsing noticeably slower. 

One known risk: the C++ version leans on value types and stack allocation for a lot of its inner-loop state. C# will put more of that on the GC heap by default. The port should prefer `struct` for small, short-lived state objects (lexer transactions, position markers) and avoid allocating per-character. `Symbol` nodes are reference types and always will be, but the bookkeeping around them should not allocate if it does not have to. Whether this matters in practice is a measurement question, not a design question. Start simple, profile, and tighten the hot paths that actually show up.

## What This Document Is Not

This is not a line-by-line port plan. It does not decide which test framework to use, it does not decide on the final grammar-authoring syntax, and it does not enumerate every C++ file and its C# counterpart. Those decisions happen during the port itself, once we have one real grammar working end to end. The point of this document is to make the constraints explicit so none of those decisions accidentally paint us into a corner where the library runs on desktop and dies on WebGL.
