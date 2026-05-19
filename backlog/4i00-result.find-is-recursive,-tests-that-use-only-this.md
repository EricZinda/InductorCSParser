# result.Find is recursive, tests that use only this to find a rule aren't checking the immediate children they are searching the whole tree

Look for tests that use this as a shortcut where they should only be checking children for the shape.

Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(result.Find(alias), Is.Not.Null,
            "The alias behind a LateBoundRule should still be findable by its name.");
        Assert.That(result.Find(alias)!.ToString(), Is.EqualTo("123"));
Shouldn't this also check that the shape is correct in case Find is recursive (or maybe it isn't by definition)?
