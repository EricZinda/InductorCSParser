# Current Fix issues

        // When emitting our own Symbol (Preserve), collect into a fresh list
        // that becomes the Symbol's children. When flattening, write directly
        // into the caller's list. When deleted, the framework gave us null and
        // we don't need to collect anything (the inner still runs so its match
        // advances the lexer).
Is this different than any other rule? If not, don't comment	

Do we have tests that cover everything that TestArchitecture says we should cover?
	
