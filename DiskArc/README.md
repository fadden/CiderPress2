# DiskArc Library #

The DiskArc library provides access to the contents of disk images and file archives.  The
focus is on Apple II and "vintage" Macintosh systems.

While disk images and file archives hold similar things, e.g. there are ProDOS file types and
access flags in ProDOS volumes and ShrinkIt archives, the way they are interacted with is
fundamentally different.

 - The contents of disk filesystems are stored in discrete blocks, which may be arranged
   sequentially or divided into tracks and sectors.  Updates to files cause blocks to be
   allocated, deallocated, or rewritten, but changing one file generally doesn't affect the
   storage of another.  File archives are generally meant to be compact and/or written as a
   stream, so simple operations like renaming a file can require shifting the contents of the
   entire archive around.
 - A disk image can be very large (even for an image from a "vintage" system) despite being
   nearly empty.  File archives often use compression to minimize their size.
 - File archive entries include the entire pathname, not just a filename, and the archive can
   have multiple entries with the same name.  The rules for determining whether a name is a
   duplicate of an existing entry are often left up to the application.  Disk image entries
   only hold the filename, are unique, and may have a hierarchical structure.

The differences mean that having a single model for working with both disk images and file
archives is difficult.  The DiskArc library uses a different set of classes for each, with a
few things in common.

The class/interface relationships look something like this:
```
 - IDiskImage (opt: INibbleDataAccess, IMetadata)
   - IChunkAccess (opt: SectorCodec)
   - IMultiPart
     - Partition
   - IFileSystem
     - IChunkAccess
     - IFileEntry
     - DiskFileStream
 - IArchive
   - IFileEntry
   - ArcReadStream
   - IPartSource
```
The relationships are more complicated than shown, e.g. most `Partitions` have an `IFileSystem`,
and filesystems with embedded volumes have an `IMultiPart`.  Note `IFileEntry` is used for both
filesystems and file archives.

## About Disk Images ##

Disk images are files that contain the contents of a floppy disk, hard drive, CD-ROM, or other
physical media.

Implementations of the `IDiskImage` interface provide access to a disk image file.  Various
classes are defined in the "DiskArc.Disk" namespace, including `UnadornedSector`for simple block
images, and `Woz` for bit- and flux-level imaging of 5.25" and 3.5" disks.

The disk image provides the data as "cooked" sector data or "raw" nibble data.  This is formed
into 256-byte, 512-byte, or 1024-byte chunks by an implementation of the `IChunkAccess` class.
If the source has raw nibbles, that data can be accessed through the `INibbleDataAccess`
interface, which uses instances of `SectorCodec` to cook the raw data.  These classes are
effectively disk device drivers (or RWTS code).

If the disk image has multiple partitions, e.g. the Apple Partition Map (APM) format used on
hard drives and CD-ROMs, an instance of the `IMultiPart` class will break it into individual
subclasses of `Partition`.

Once we have access to chunks, we need to read the filesystem.  Instances of the `IFileSystem`
class can be found in the `DiskArc.FS` namespace.  Individual file entries are presented through
a filesystem-specific subclass of `IFileEntry` (this interface is also used by the file archive
classes).

The `IFileSystem` interface provides calls to manipulate files: create, delete, move, and so on.
It also allows the data and resource forks to be opened for reading and writing.  The role of
this class is similar to a GS/OS File System Translator.

When a file on a disk image is opened, the library returns an instance of `DiskFileStream`.  This
class is a subclass of the standard .NET `Stream` class, and behaves just like a file opened on
the host filesystem: read, write, seek, truncate, etc.  The calls may behave a little
differently due to limitations of the filesystem, but the expectation is that the object can be
passed to any code that takes a `Stream`.

### About Filesystems ###

The filesystem implementations are initially in "raw access" mode.  This allows direct access to
the blocks or sectors that make up the filesystem.  Applications wishing to perform sector edits
or do their own filesystem scanning would use this mode.  The filesystem code is not allowed to
cache any filesystem data when in this mode.

When switched to "file access" mode, access to the raw blocks becomes read-only.  The code scans
the filesystem, doing either a quick "read a few things to see if it looks right" pass, or a
very thorough full-disk validation pass.

All filesystems are considered hierarchical, though for flat filesystems like DOS they simply
have a root directory that holds all of the files.  This simplifies the API, e.g. the call to
find a file by name always takes a directory.

Nibble disk images are allowed to have bad blocks.  Attempting to read or write bad blocks causes
an appropriate exception to be thrown.

Sometimes filesystems have other filesystems embedded within them.  One example is DOS.MASTER,
which embeds multiple DOS disks in a ProDOS volume.  A modified version of DOS is required to
access these.  Another example is DOS hybrids, where DOS sits on tracks 17-34, while another
operating system (ProDOS, Pascal, or CP/M) sits on tracks 0-16.  The filesystem object returned
represents the "primary" filesystem, with the embedded filesystems available through a
secondary API.

## About File Archives ##

File archives hold a collection of files and their associated attributes, such as file types
and dates.  The files are usually compressed to minimize storage space requirements.

Implementations of the `IArchive` interface allow an archive to be opened and examined.  Changes
are performed by opening a transaction, making the desired edits, and then committing the
transaction.  An updated copy of the archive is written to a new file, which replaces the original
when the entire transaction completes successfully.  If the transaction is cancelled or fails,
the original file remains unmodified.

Entries in an archive are returned as an archive-specific implementation of `IFileEntry`.  The
library provides a way to make trivial changes, such as altering a file type, directly to the
original archive, but in practice this isn't used.

File archive entries are typically compressed, so files opened in an archive are read-only and
cannot be seeked.  The `ArcFileStream` class returned by the library is a subclass of `Stream`,
but has the `CanWrite` and `CanSeek` features disabled.  When a file is read to the end, any
associated checksum will be verified, and an exception will be thrown if the file appears to
be damaged.

Because all modifications are made through the transaction mechanism, the reading of input files
is deferred until the changes are committed.  Instead of passing in an open `Stream` for each
data file, applications pass an instance of the `IPartSource` class, which represents the contents
of one fork of one file.  During the commit, the library will ask the part source object to open
the stream, and will close the file as soon as it's no longer needed.  If compression fails to
reduce the stored size of the file, the input file will be closed and re-opened.

### Single-File vs. Multi-File Archives ###

When people talk about file archives, they usually mean formats that hold multiple files.  ZIP,
ShrinkIt, Stuffit, and tar are examples.

The DiskArc library also supports single-file archives, such as gzip and AppleSingle.  These
are meant to hold a single file and its associated attributes.  The library uses the same classes
to represent these, but certain operations won't work.  For example, you can't create an entry
in a gzip file, or remove an entry.  When you create a new gzip instance, it will already have
an entry, and you can add data to it.

The Mac OS X ZIP utility essentially creates archives in archives: files with resource forks or
extended attributes are stored as two entries.  The second entry's path is prefixed with
`__MACOSX`, and the filename is prefixed with `._`, forming an AppleDouble "header" file.  The
DiskArc library doesn't give these special treatment.  It's up to the application to decide how
it wants to work with these archives.

## Library Fun Facts ##

The library never opens files or physical devices directly.  The caller provides a `Stream` or
other object that provides the data.  This keeps file management logic out of the library, and
allows memory streams to be passed around for smaller files.  It also makes reading a file
archive that lives on a disk image as easy as reading one that lives on the host filesystem.

The filesystem code allows multiple files to be open at once for reading and writing, but
individual forks may not be opened more than once for writing.  Having both forks of an extended
file open at the same time for writing is allowed.  File archives have no limitation on
simultaneous opens.

The library has no mutable global state, and may be used from multiple threads simultaneously.
However, individual object instances are not reentrant, so access to filesystems and file data
objects must be single-threaded.  In particular, a disk image and everything within it must be
accessed from a single thread.

Many library calls take an `AppHook` object as an argument.  This provides a way for the
application to pass configuration options in, and for the library to send debug log messages
to the application.

The `IChunkAccess` instance published by `IFileSystem` is actually `GatedChunkAccess`, which
wraps the actual chunk object with an access gate.  The gate will be open when the filesystem
is in raw-access mode, and read-only when it's in file-access mode.  This allows an application
to view disk blocks while handling files, but prevents it from making changes directly to the
filesystem.

The `IDiskImage` instance also publishes an `IChunkAccess` object.  This is meant to be used in
situations where the disk's sector format can be determined, but the filesystem is unknown.  If
the filesystem is successfully identified, the disk image object's chunk access will be closed.

Many minor problems can be identified when processing disk images, filesystems, partitions,
and file archive.  These don't require failing entirely, but may warrant caution or at least
awareness.  Serious problems cause files, disks, or archives to be marked as "dubious", which
allows them to be read but not written.  Files may also be marked as "damaged", which means
they're not accessible at all.  Minor problems are logged in human-readable form in a `Notes`
object associated with the disk image, filesystem, partition, or archive.  Applications can
display these to the user.

TODO: the `VolumeUsage` class provides an annotated volume bitmap, showing which blocks or sectors
are in use, and by whom.  Requires a full scan and file-access mode.  (Not currently available.)

### IDisposable ###

C# .NET has a "managed" heap that uses garbage collection to discard unused objects.  The
garbage collection runs when memory pressure indicates that it's needed.  In C++, destructors
run immediately when an object goes out of scope, but in C# the nearest equivalent (finalizers)
may not run for a long time.  This becomes an issue when objects are holding resources other than
memory buffers, such as file streams, because files can be held open indefinitely.

Objects can implement `IDisposable` to facilitate cleanup.  Disposable objects can be placed
in `using` statements so that, when they go out of scope, their `Dispose()` method is called
automatically, even if an exception is thrown.

All DiskArc library objects that "own" file streams implement this interface.  Many of them also
have finalizers, primarily to ensure that the objects are explicitly disposed: if a finalizer
determines that it is being discarded by the garbage collector, it will log a warning and possibly
fire an assertion.  These won't impact a release build; they exist strictly to bring the lack of
proper cleanup to the attention of the developer.

There are situations where an application will have access to a disposable object, but should
not dispose of it because it doesn't own it.  As a rule, disposable objects returned by a method
call must be disposed by the application.  Disposable objects that are obtained from an object
property, such as the filesystem reference available from an analyzed `IDiskImage`, must not be
disposed by the application, because its lifetime is tied to that other object.

### Extension Methods ###

If a certain operation is used often, but doesn't need to be part of the interface definition,
it may be provided by the library as an extension method.  For those not familiar with C#,
extension methods enable you to add a method to an existing class without creating a subclass.

For example, one of the more common operations on an `IFileSystem` is to find a file by name.
This doesn't need to be part of the interface, because code can simply walk through the list
of filenames.  Instead of putting it in a separate library, however, the `DAExtensions` class
defines a `FindFileEntry()` method that can be called directly on an `IFileSystem` object.  So
it functions as if it were part of the interface, even though it could be implemented in an
entirely different library.

#### MISC - TBD

The disk images and archives must not be modified unless a "write" operation is performed.  This
may seem obvious, but in some cases minor corrections are made in the course of scanning files,
such as correcting the file size reported in the disk catalog.  In other cases the write is
expected, e.g. a bit is cleared in the HFS master disk block when it is mounted, and set when
it's unmounted, so that a consistency check can be performed automatically after a power failure.
Any such changes must not be written to disk unless the caller explicitly changes something else,
e.g. altering a file date could also cause the storage size to be corrected.

Exceptions:
 - IOException and sub-classes mean "something unexpected happened during an operation".
 - ArgumentException and sub-classes mean you passed bad arguments in.
 - DAException mean mis-use of APIs, like trying to switch to raw-access mode
   with files open, or an internal error.

Disk analyzer tries to find a filesystem that fills a certain space.  It will not look for
DOS 3.3 in a 32MB volume, or ProDOS in a 40MB volume.

## Filesystem Implementation Notes ##

Modification dates on files are *not* updated automatically when files are modified.  The
assumption is that the library is being used for file archiving, not general file access.

### DOS ###

DOS is well supported, for both reading and writing, but can be tricky.  A solid effort is made
to handle bad sectors and filesystem corruption.

Files with types I (Integer BASIC), A (Applesoft BASIC), and B (binary) have explicit lengths
embedded in the first sector.  Sequential text files with type T (text) end at the first
occurrence of a $00 byte.  Other file types do not have a length, so the end of file is
determined by counting up the number of allocated sectors.

What makes this even trickier is that some files effectively have two lengths.  Some games were
distributed as a 'B' file with a loader program: the embedded file length was only a few hundred
bytes, so that when the file was BRUN, DOS would load the first part and then stop.  The loader
would then walk through the list of allocated sectors to load the rest.  Correctly handling files
of this type requires treating the file in two different ways.

By default, the embedded file length is used.  Opening a BASIC or binary file will present a
file that looks exactly the way it would on a ProDOS disk.  File reads are offset by 2 or 4 bytes
to skip past the header at the start of the file.  A newly-created file is typeless (uses type 'S')
and has zero-length.  Changing the type to I/A/B doesn't cause the first sector to be allocated,
so there won't be anywhere to store the load address for a 'B' file.  (There's also nowhere to
store the length, but we know the length is zero.)  To properly form an empty I/A/B file, create
it, set the type, open it in cooked mode, and issue a zero-length write.

An alternate "raw data" mode can be selected when a file is opened.  In this mode, all files are
treated as sector data, regardless of file type.  The end of file will always be a multiple of
the sector size (256).

Random-access text files must be accessed in "raw data" mode, as the end-of-file marker is
determined by the first $00 byte.  While any file may have sparse sectors, in normal
(command-line) usage, only random-access text files do.  Writing a zeroed-out sector does not
cause the block to become sparse, but seeking past the EOF and then writing can do so.

The features of the Seek() call that allow examination of sparse regions only work in "raw" mode.

Extending a file with SetLength() will only have an effect on I/A/B files, and only in "cooked"
mode; for these the embedded EOF value is updated.  In no event will it add additional sectors
to the file.  Partially truncating a sparse file may cause the file to become shorter than
expected, because the "raw" length is determined by the position of the last non-sparse sector.

The file's type is cached when the file is opened.  Changing the file type after a file is opened
will not affect file access for that stream.

Filenames may be up to 30 characters long.  DOS requires that filenames start with a letter and
not include a comma.  In practice, filenames can include inverse/flashing text and control
characters (notably backspace), though it may not be possible to open them from the command line.
The only hard limitation is that trailing spaces are trimmed.  In the API, the "cooked" filename
will strip the high bits and convert control characters to the "control pictures" block.  This
effectively loses inverse/flashing, so if those are desirable it's necessary to use the "raw"
filename interface, which has no restrictions.

DOS file types are mapped to ProDOS equivalents.  The auxiliary type field is only supported
for 'B' files, though 'A' files will report $0801 for compatibility with ProDOS.

The DOS catalog scan normally stops when the first unused entry is encountered, but that behavior
is configurable.  Some disks may show garbage entries and appear to be damaged if the scan
continues past an unused entry.

Hybrid volumes, where DOS peacefully overlaps with ProDOS, Pascal, or CP/M, can be detected
through the "embedded volume" mechanism.

### HFS ###

HFS is well supported, for both reading and writing, on volumes up to 4GB.  The initial file
scan can be skipped unless the volume wasn't unmounted cleanly.  Some attempts are made to
handle corrupted structures and avoid corruption when bad blocks are encountered.

HFS does not support ProDOS file types or sparse files.  A scheme for storing ProDOS types
in the creator and file type fields is defined, but it is up to the application to perform
the translation if desired.

Using `SetLength()` to extend a file will cause additional storage to be allocated immediately.

Filenames may be up to 31 characters long, and include any character except colon (':'), except
for the volume name which is limited to 27 characters.  Names may have embedded null bytes, though
this is discouraged.  The character set used is assumed to be Mac OS Roman, and the "cooked"
filename will have an appropriate transformation to Unicode applied.  Control characters are
converted to the Unicode "control pictures" block.

Directories are not files.  They do not have a length or a file type.

The original CiderPress (and presumably a number of other applications) used libhfs v3.2.6 by
Robert Leslie.  That code incorrectly handled Daylight Saving Time calculations for file
timestamps.  This means that CiderPress II will report some timestamps offset by an hour from
some other utilities.  (They will match the values shown on a Mac or Apple IIgs.)

### ProDOS ###

ProDOS is very well supported.  The filesystem scan will detect a wide variety of anomalies,
and a best effort is made to handle bad blocks and various kinds of filesystem corruption.  All
interfaces are fully supported.  The initial file scan can be disabled to speed up volume opens.

Extended files, i.e. files with resource forks, have both ProDOS and HFS file types.  These may
be edited independently.

The implementation is similar to the ProDOS FST in GS/OS.  Blocks filled with zeroes will be
written as "sparse" blocks to reduce the storage required.  Lower-case filenames are allowed,
via the GS/OS extension.  Extending a file with SetLength() will add a sparse area at the end.

Embedded DOS volumes, such as those created by DOS.MASTER, can be detected automatically.


## Archive Implementation Notes ##

The DiskArc library does not prevent duplicate entries from being created unless the archive
format specifically disallows duplicates.  Currently, none do.  (The Binary II code will
complain though.)  Applications are free to impose more stringent requirements.

#### TBD
- AppleSingle doesn't require any actual data, but is currently configured to provide an
  empty data fork because all of the instances I've seen have that.
- GZIP requires data.
- GZIP DataLength will be -1; use GetPartInfo() instead, but don't use value to size a buffer.
