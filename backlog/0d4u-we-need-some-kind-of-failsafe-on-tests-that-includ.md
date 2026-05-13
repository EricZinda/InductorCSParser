# We need some kind of failsafe on tests that include unicode literals

Some editors and tools rewrite them and we don't want that. We need an assert or test that runs everywhere we have them to be the canary when this happens
