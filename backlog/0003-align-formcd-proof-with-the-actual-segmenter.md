# Align the FormC/FormD position-map proof with the actual segmenter

`docs/MappingPositionsAfterNormalization.md` conditions the lockstep proof on
Unicode 16's complete default UAX #29 extended-grapheme-cluster rules. The
built-in `GraphemeSegmentation` instead deliberately implements the revision 41
rule set over Unicode 16 property data and omits GB9c to match .NET 10. The
proof therefore doesn't currently establish its premise for the implementation
it claims to justify.

Describe the built-in and runtime segmentation profiles exactly, state the
canonical-boundary-invariance property the optimization actually needs, and
prove or comprehensively test that each selectable segmenter satisfies it with
the corresponding normalizer. Don't add GB9c merely to make the prose true if
that would break the intentional .NET compatibility policy. If the runtime
configuration can't guarantee this property, use the comparison walker there
or restrict the optimization to verified configurations.
