- Fixes
Don't use the term "pins", use "verifies"
	
Replace Preserve-typed (and the other options) with the actual types when you speak about them: FlattenType.Preserve.  Put this in the docs as the right way to alk about htem	

    public override string ToString()
    {
        var ranges = _ranges;
        if (ranges == null || ranges.Length == 0) return "[]";
        var sb = new StringBuilder();
        sb.Append('[');
        for (int index = 0; index < ranges.Length; index++)
        {
            if (index > 0) sb.Append(',');
            var interval = ranges[index];
            sb.Append(RenderCodepoint(interval.Low));
            if (interval.High != interval.Low)
            {
                sb.Append('-');
                sb.Append(RenderCodepoint(interval.High));
            }
        }
        sb.Append(']');
        return sb.ToString();
    }
This seems like it should stop at some piont so it isn't ridiculous
	

    public override int GetHashCode()
    {
        var ranges = _ranges;
        if (ranges == null) return 0;
        var hash = new HashCode();
        for (int index = 0; index < ranges.Length; index++)
            hash.Add(ranges[index]);
        return hash.ToHashCode();
    }
Sghouldn't this cache?

        while (true)
        {
In StringChars.  I don't like it...
	
 Escape-start runes (if a
        // grammar has them) are typically outside the stopper set, which
        // means ~_stopperSet already covers them. We don't need a separate
        // union.
typicly sounds scary!

Are there properties, constructors members or methods that aren't used by aything?
	
