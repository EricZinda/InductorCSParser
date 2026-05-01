- Doc fixes
ScanUntil needs different error messages for sub rules I think
    internal void AdvanceUntilRuneIn(RuneSet candidates, char[]? bmpCandidates)
    internal int AdvanceWhileRuneIn(RuneSet set)
    internal int AdvanceWhileTokenIn(RuneSet set)
AdvanceUntilLiteralCandidateIn	
FindNextLiteralCandidate
	AnyLiteralMatchesAt
All of those are performance optimizations right? Are they only used by the state machine? They need a comment that says as much without naming state machine
	
