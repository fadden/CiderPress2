# Disk Image File Implementations #

This directory holds implementations of disk image formats.  Some are block/sector images, some
are nibble images, some are both.  Disk images may hold single filesystems or multi-partition
formats.

## Adding New Implementations ##

Implementations need to implement `IDiskImage`.  Nibble formats also need to implement
`INibbleDataAccess`.

If the format can be either sector or nibble, it's useful to split the implementation into
two classes, so that we're not passing around objects with nibble-specific interfaces when they
don't actually have nibbles.  See TwoIMG / TwoIMG_Nibble for an example.

Suggested procedure:

 - Start with UnadornedSector or UnadornedNibble525 as appropriate.
 - Write TestKind(), OpenDisk(), and AnalyzeDisk().
 - Add some sample files to the TestData directory.
 - Write a "prefab" test that opens all of the sample files and does trivial checks (see the
   existing tests for examples).
 - Write whatever CreateDisk() routines are relevant.  Implement Flush().
 - Write "creation" tests that create disks and fiddle with them.
 - Update FileAnalyzer to recognize the extension.  Verify that cp2 can open them.
 - Update cp2 "create-disk-image" to create images.
 - If the disk format has metadata, update the cp2 "get-metadata" and "set-metadata" calls to
   read/write the fields.
 - Update the cp2 manual with the file extension and metadata information.
