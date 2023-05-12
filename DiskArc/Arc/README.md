# Archive Implementations #

This directory holds implementations of various file archive formats.  Start with the IArchive
class to understand the interfaces.

## Adding New Implementations ##

All archive implementations have classes that implement two interfaces:

 - IArchive
 - IFileEntryExt

Reading from archives is handled by ArcReadStream, which is generic.

Suggested procedure:

 - Start by defining place-holder implementations for both interfaces.
 - Write the code that scans the contents of the archive.
 - Add an entry to FileAnalyzer, so the archives are automatically detected.
 - Write "prefab" tests that read the contents of existing archives.  Add the tests and some
   sample archives to the test set.
 - Write the code that extracts files and handles file attribute queries.  Add tests.

For a read-only implementation, you're mostly done.  For read-write:

 - Implement transactions.  This is somewhat complicated, so it's best to follow the pattern
   in a similar archive format.

Update general tests and the applications:

 - Add driver to TestArcInterfaces.
 - Update the archive-specific commands in "cp2", like `create-file-archive`.
