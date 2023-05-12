# Multi-Partition Implementations #

This directory holds implementations of multi-partition disk structures.  In some cases, like
Apple Partition Map, these are data structures on the disk.  In others, like DOS800 or CFFA,
the structure is implied by geometry and filesystem placement.

## Adding New Implementations ##

Implementations need only implement the `IMultiPart` interface.

In some cases it may be useful to create a subclass of `Partition` to store additional information,
such as partition names.

Suggested procedure:

 - Create a place-holder implementation of the interface.
 - Write the code that scans the contents of the partition structure.
 - Add an entry to FileAnalyzer, so the disks are automatically detected.
 - Write "prefab" tests that read the directory of existing disk images.  Add the tests and
   sample disk images to the test set.

Partition layouts are not modifiable, so there are no "write" methods.
