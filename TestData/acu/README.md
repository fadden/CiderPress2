# AppleLink Conversion Utility Samples #

"IconEd.ACU" - found online.

"test.bad.acu" - created from a collection of small test files, using
ACU v1.06 in an emulator.  This demonstrates the problems with file data
CRCs.  The records with names ending in ".bad" were altered with a hex editor,
and should all fail to extract.  When ACU unpacks them, it reports errors on
PAT255.BAD and PAT256.BAD, but not PAT257.BAD.  Not coincidentally, the
CRCs on files 257 bytes and larger appear to be generated incorrectly by ACU.
FWIW, ShrinkIt v3.4 extracts all files without reporting errors, so it appears
to be ignoring the CRCs.  The archive also has 1030 extra bytes at the end of
the file that were left there by ACU.
