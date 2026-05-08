# What's in Original/

The "upstream" we're porting is the TOML 1.0 specification itself, not a
specific reference implementation. TOML's canonical home is https://toml.io
and the v1.0.0 spec we ported against is at https://toml.io/en/v1.0.0,
published 2021-01-11. The complete formal grammar lives in `toml-1.0.0.abnf`
in this directory, copied verbatim from the spec.

This is different from the typical E2ESamples shape (where Original/ holds
the source of one specific parser implementation we copied). For TOML we
discovered the format through toml.io rather than through any one
implementation, and the spec's ABNF is the cleanest "original" artifact to
diff our rewrite against. The line-count comparison in the parent README is
the ABNF row-count vs the InductorParser grammar's source line count.

For behavioral comparison we reference Tomlyn (BSD-2-Clause), discovered
through https://www.nuget.org/packages/Tomlyn. Tomlyn isn't copied into
Original/ — when the Tests/ project runs comparison tests, it pulls Tomlyn
in via NuGet so we always compare against the published library, not a
snapshot we'd have to keep in sync.

License attribution for the spec is in `LICENSE.md`.
