# Example: AddFile #

Usage: `AddFile <archive> <file-to-add>`

This adds a file to a file archive, or to the root directory of a disk image.  This is a simple
example, intended to illustrate the basic operation of the DiskArc library.

It does not support multi-partition formats, does not provide a way to access archives within
archives, does no data conversion, and does not set file attributes other than the file
modification date.  The file's name is not altered, and so must be compatible with the target
archive or filesystem.

The program does not explicitly check to see if a file with the same name already exists.  As a
result, adding the same file twice will succeed for archives, but fail for disk images.
