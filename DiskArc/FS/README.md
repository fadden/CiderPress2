# Filesystem Implementations #

This directory holds implementations of various filesystems.  Start with the IFileSystem class
to understand how they work.

## Adding New Implementations ##

All filesystem implementations need classes that implement three interfaces:

 - IFileSystem
 - IFileEntryExt
 - DiskFileStream (formerly IFileDesc)

Suggested procedure:

 - Start by defining place-holder implementations for all three interface classes.
 - Copy the generic parts out of an existing filesystem that seems similar, e.g. DOS or ProDOS.
   You need the Dispose() functions and the open-file tracking.
 - Write the code that scans the contents of the directory structure.
 - Add an entry to FileAnalyzer, so the disks are automatically detected.
 - Write "prefab" tests that read the directory of existing disk images.  Add the tests and
   sample disk images to the test set.
 - Write the code that reads files and handles file attribute queries.  Add tests.

For a read-only implementation, you're mostly done.  For read-write:

 - Implement Format(), so you can create new disk images.  Add tests.
 - Write the code that modifies files: Create, Write, Delete, Move, AddRsrc.  Add tests.

Update general tests and the applications:

 - Add driver to TestFSInterfaces.
 - Check switch statements in DAExtensions.
 - Update the filesystem-specific commands in "cp2", like `create-disk-image`.
 - Update TestAdd in "cp2".

## Hybrids ##

The multi-partition hybrid formats, like DOS+{ProDOS,Pascal,CP/M} and DOS MASTER embedded volumes,
are handled here as IMultiPart sub-classes returned by an IFileSystem call.  Strictly speaking,
they're not multi-partition formats, but IMultiPart has the needed pieces.  See the notes in
the "Multi" directory for additional information on the interface.
