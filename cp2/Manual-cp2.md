# CiderPress II Command-Line Utility Manual #

Contents:
 - [Introduction](#introduction)
   - [Frequently Asked Questions](#frequently-asked-questions)
 - [Operational Details](#operational-details)
   - [Disk Images vs. Archives](#disk-iamges-vs-archives)
   - [Add, Extract, Import, Export](#add-extract-import-export)
   - [Extended Archive Naming](#extended-archive-naming)
   - [Multiple Archive References](#multiple-archive-references)
   - [Filenames and Wildcards](#filenames-and-wildcards)
 - [Commands](#commands)
 - [Options](#options)
 - [Special Arguments](#special-arguments)
   - [Disk Image Sizes](#disk-image-sizes)
   - [Filename Extensions](#filename-extensions)
   - [Filesystem Types](#filesystem-types)
   - [Import and Export](#import-and-export)
 - [Resource Fork and Attribute Preservation](#resource-fork-and-attribute-preservation)
   - [Finding Resource Forks](#finding-resource-forks)
   - [Access Flags](#access-flags)
 - [Metadata](#metadata)
   - [2IMG](#2img)
   - [WOZ](#woz)
 - [Appendix](#appendix)
   - [AppleWorks Filename Formatting](#appleworks-filename-formatting)
   - ["TO DO" List](#to-do-list)

## Introduction ##

CiderPress II is a utility for managing disk images and file archives, with
a focus on the Apple II and early Macintosh.  Disk images of floppies,
hard drives, CD-ROMs, and other formats are supported, as are popular file
archive formats like ShrinkIt.  For easier interaction with modern storage
formats, ZIP and gzip are also supported.

Archives and disk images may be nested.  For example, if you have a ZIP
archive with a multi-partition hard drive image with a ProDOS partition
that has a ShrinkIt archive, you can access the ShrinkIt archive directly
from the command line without having to unpack everything first.  Updates
to the various disk image and archive "wrappers" are handled automatically.

To reduce verbiage in this document, disk images and file archives are
collectively referred to as "archives" here.  A distinction is made for
certain commands that only work with one or the other.

### Frequently Asked Questions ###

*Q*: How do I list the contents of a disk image or file archive?

*A*: Use the `list` or `catalog` commands.  To generate a listing for a large
collection, use the `mdc` command.  These commands take options that control
whether they descend into disk images and file archives found inside.

*Q*: How do I convert a disk image to a different disk image format?

*A*: Create the destination disk image with `create-disk-image`, making sure
it's the same size as the original.  Then, copy the contents with
`copy-blocks` or `copy-sectors`.

*Q*: How do I convert a disk image to a file archive, and vice-versa?

*A*: Create the destination file with `create-disk-image` or
`create-file-archive`, then use the `copy` command with no arguments.  This
copies every file from the source to the destination.  Note however that
directories are not created as separate entities in file archives, so empty
directories will not be copied to those.

*Q*: How do I perform sector editing on disk images?

*A*: Use the `read-sector` or `read-block` command to generate a hex dump of
the contents.  Edit the hexadecimal values, then use `write-sector` or
`write-block` to write the contents back to the disk.

*Q*: What is the difference between `extract archive.shk DIR:MYFILE` and
`extract archive.shk:DIR MYFILE`?

*A*: They both extract the same file.  The first command extracts the file as
"DIR/MYFILE", so it will be extracted into a subdirectory called "DIR", which
will be created if it doesn't already exist.  The second command extracts the
file as "MYFILE" into the current directory.  If `--strip-paths` is set then
the commands have equivalent behavior.

*Q*: Can I have file types and resource forks in ZIP archives?

*A*: Yes, using the `__MACOSX` AppleDouble storage convention.  So long as the
`--mac-zip` option is enabled, this happens transparently.

*Q*: How do I convert an Applesoft listing in a text file to BASIC on disk?

*A*: Use the `bas` specification for the `import` command.  For example,
`cp2 import mydisk.po bas Listing.txt`.  The importer expects plain text,
with one line of BASIC code per line of text.  The importer does not attempt
to validate syntax beyond ensuring that each line is numbered.

*Q*: Why is my random-access DOS text file truncated?

*A*: You need to specify `--raw` for DOS text files, or the file scanner will
think the file ends at the first $00 byte.  Random-access files use
fixed-length records, but DOS doesn't have a way to store the record length,
so it must be specified on the command line.  For example, to convert the
ANIMALSFILE from the early DOS System Master disks, use
`cp2 export --raw <archive> rtext,len=80 ANIMALSFILE`.


## Operational Details ##

These sections explain a bit about how CiderPress II works and what it can do.

### Disk Images vs. Archives ###

Updates to disk images and file archives are handled differently.
Disk images are usually stored as a series of blocks or tracks with a
"vintage" filesystem, such as ProDOS or HFS, used to store the file data.
Files can be added and deleted freely without affecting the bulk of the
data in the image.  In contrast, file archives hold a list of files, often
compressed, and simple actions like renaming a file can require shifting
a lot of data around.

Changes to disk images are made directly, as if the image were a mounted
filesystem.  If you try to add 3 files to a disk image, and cancel the
process before adding the third file, the disk image will have the first
two files on it.

Changes to file archives are made by creating a new archive, and renaming
it in place of the original when the update has completed.  If the operation
is cancelled partway through, none of the changes will be retained.

One important consequence of this arrangement is that, if you kill the process
while updating a file archive, you will at worst be left with a
partially-written temporary file.  If you do the same while updating a disk
image, you can corrupt disk data structures in the same way you would if the
power failed while writing to the disk.  The filesystem code does its best to
order operations so that interruptions are as low-risk as possible, but it's
wise to avoid interrupting add, import, copy, move, and delete operations.
(The program catches Ctrl+C for those operations.)

### Add, Extract, Import, Export ###

There are four distinct operations for adding and extracting files:

 - Add: add a file to an archive without modification.  Attempt to restore
   file attributes from saved metadata.
 - Extract: extract a file from an archive without modification.  Attempt to
   preserve file attributes.
 - Import: add a file to an archive, converting its format.  For example,
   the end-of-line markers in a text file might be changed from CRLF to CR,
   or an Applesoft BASIC program could be converted from a text file to
   tokenized form.
 - Export: extract a file from an archive, converting it to something new.
   This could be a simple adjustment to a text file, or a conversion from
   Apple II hi-res to GIF or PNG.

Utilities such as NuLib2 and the original CiderPress blend the operations
together, which can lead to some ambiguous behavior.

### Extended Archive Naming ###

When a command takes `<ext-archive>` as an argument, the archive name may
be a colon-separated path to an archive within an archive.  For example,
consider an archive with the following structure:

    archive.zip
      multipart.po
        partition 1: HFS "MacStuff"
        partition 2: ProDOS "II.STUFF"
          subdir
            myfiles.SHK

The "list" command can be applied to each individual piece:
 - `cp2 list archive.zip`  # lists contents of ZIP file
 - `cp2 list archive.zip:multipart.po`  # lists partitions
 - `cp2 list archive.zip:mulitpart.po:HFS_Part`  # lists contents of named APM partition
 - `cp2 list archive.zip:mulitpart.po:2`  # lists contents of 2nd (ProDOS) partition
 - `cp2 list archive.zip:mulitpart.po:2:subdir:myfiles.SHK`  # lists contents of NuFX archive

The named item must be a disk image or file archive.  There are no
restrictions on what may be stored inside what, e.g. you can access ShrinkIt
archives in disk images stored in bigger disk images stored in a ZIP archive.

If a filename contains a colon, it must be escaped with "\" (which may need
to be doubled-up for some command-line shells, e.g. `cp2 list foo\\:bar.zip`
to open a file called `foo:bar.zip`).  This should only be an issue for host
files and DOS 3.2/3.3 disks, since most other filesystems disallow colons.
All pathnames in disk images and archives can be specified with ':' as
the path separator.

gzip is handled "transparently", i.e. if you specify `foo.po.gz` you will
get `foo.po` automatically, without having to specify `foo.po.gz:foo.po`.
This is convenient, but also means that you cannot simply uncompress
a gzip file.  ShrinkIt disk images (.SDK) get similar treatment, and
generally act as a disk image rather than as a file archive with a disk
image entry stored inside.  This behavior can be controlled with the
`--skip-simple` option, e.g.  using `--no-skip-simple` will allow a gzip
file to be uncompressed (but will use the filename stored in the archive).

Filename matching is always case-insensitive, even for case-sensitive
formats.

Partitions and embedded volumes can be identified by index or, for named
APM partitions, by name.  Index numbers start at 1.

When working with disk images, the ext-archive argument may end in a
directory, to specify a subdirectory as the root for the operation.
This doesn't work with file archives.  It is not possible to move above
the root with "..".

Note for certain Windows command shells: some shells allow `C:/src/test`,
`C:\\src\\test`, and `/c/src/test` to all mean the same thing.  If the
filename includes a ':', however, the conversion on the last form is
suppressed by the shell.  Using one of the other forms may be necessary
when specifying an archive within an archive.  Further, specifying "/" by
itself, e.g. to specify the root directory as the target of a file move,
may get converted to a shell-specific directory.  It should be possible
to just use ":" instead.

#### Multiple Archive References ####

When copying files or blocks between archives, it is sometimes desirable to
use a single archive file as both source and destination.  For example, you
might copy files between two disk images stored inside the same ZIP archive.
You might even want to copy files between directories in a single disk
image, or copy blocks within a disk image.

It would be dangerous to open the archive file twice, because some parts
of archives and disk images are cached in memory when the files are opened.
Updates to one copy would not be visible to the other, potentially leading
to corrupted data.

To handle this situation safely, the program must detect this situation
and only open the relevant file archives and disk images once, using the
same data structures for both source and destination.  For this to work,
it is essential that the ext-archive specifiers on the command line be
written in exactly the same way.  In most situations this works just fine,
but there are ways to sneak around it.  For example, on a UNIX system, you
could reference an archive through a symbolic link, making it appear that
two different files are being used.  (A subtle version of this involves
case-sensitive vs. case-insensitive filesystems.  "FOO" and "foo" might
be the same file or different files.)

Copying files from a file archive that lives within another file archive is
especially problematic, because the "child" archive must be open for the
duration of the transaction on the "parent"... but you can't start a
transaction while a file is open.  Handling this correctly requires
extracting the "child" archive to a temporary file for the duration of the
copy action.  (Disk images don't have this limitation.)

We currently reject copy commands that use the same host file for both source
and destination, and recommend against trying to circumvent the detection.

### Filenames and Wildcards ###

All references to filenames in archives are partial pathnames.  Do not start
them with "/" or ":".

When adding files, filenames may include wildcards if your command shell
allows them.  When referencing files in an archive, e.g. for extraction or
deletion, the '*' and '?' values have the usual "glob" meanings, allowing
a match on an arbitrary string or an arbitrary character, respectively.

For example, extracting `files/*.txt` will extract `files/foo.txt` and
`files/bar.txt`, but not `foo.txt` or `files/foo.bin`.  These rules apply
to both disk images and file archives, even though file archives don't
really have a directory structure.  All matches are case-insensitive,
even for formats that are case-sensitive.

The wildcard processor marks all matching files before the action starts.
This is different from shell behavior, where `ls *.t* *.tx*` will list
all files named `*.txt` twice.  If a pattern doesn't match any files,
an error will be reported, and the entire action will be cancelled.

In most cases, the wildcard characters will need to be escaped in your
command shell, e.g. with a backslash or by double-quoting the entire string.
Wildcard characters can themselves be escaped if, for example, you need
to extract a file named "foo*bar".

Pathname directory components may be separated by '/' or ':'.  If you need
to use one of these in a filename, e.g. you want to extract `Docs:Files 5/23`
from an HFS volume, escape the non-separator with a backslash (and, if
necessary, quoting to ensure the backslash isn't consumed by the command-line
shell): `"Docs:Files 5\/23"`.

#### Filename Adjustment ####

When adding, extracting, or copying files between volumes, it's
possible that the name of a file is not valid on the destination system.
The filenames are automatically "adjusted" to be valid.  The way this
is done is filesystem-specific.  Empty filenames are generally replaced
with "Q", and filenames that must start with a letter will have an "X"
prepended if necessary.  Invalid characters are replaced with ".", "?",
or "_" depending on circumstances.

Files extracted from archives will have invalid characters replaced with
an underscore ('_').  The set of invalid characters varies from system
to system.  For example, UNIX allows anything but '\0' and '/', while
Windows outlaws control characters and `|/\?*:<>"`.  The set is determined
by the operating system, not the filesystem, so some bad interactions
are possible.  The AppleSingle and NAPS preservation modes can record
or escape the original filename during extraction, but do not preserve
invalid characters in directory names.

Control characters used in filenames for files in archives are converted to
the Unicode "Control Pictures" range (U+2400 - U+243F) when the files are
listed or extracted, regardless of their legality on the host filesystem.
Control Pictures are converted back to control characters when files
are added, if the destination format allows them.  This allows any host
filesystem that supports the Unicode BMP to store filenames with the full
range of control characters, including NUL ('\0').


## Commands ##

The first argument is a command to execute, followed by zero or more options.
This is usually followed by an ext-archive specification, and perhaps
some filenames.

This section lists the commands, with usage information, a list of relevant
options, and some examples.  Options that are broadly applicable, such as
`--verbose`, are not included for each command.  All of the options are
described in detail in a [later section](#options).  Every option can be
passed to any command, though in many cases they will have no effect.

For a list of filename extensions used for file archives and disk images, see
[Filename Extensions](#filename-extensions).

----
#### `add`|`a`

Adds one or more files to the archive, without altering their contents.
Directories will be recursively traversed.

Usage: `cp2 add [options] <ext-archive> <file-or-dir> [file-or-dir...]`

If the archive does not exist, and the filename is a simple (non-extended)
archive name that ends in a recognized multi-file archive extension
(i.e. not a disk image or a single-file archive format like AppleSingle),
a new archive will be created.

If a file with the same (case-insensitive) name is found in the archive,
the file will be either be skipped, or allowed to replace the existing entry.
In interactive mode, the user will be prompted for the desired behavior.
Plain files are not allowed to replace directories, and directories are not
allowed to replace plain files.

Resource forks and file attributes will be located according to the enabled
`--from-*` options.  If the target archive does not support resource forks,
e.g. ZIP archive or DOS 3.3 filesystem, the resource forks will be discarded
with a warning (but note the `--mac-zip` option).  To add the files in
their current form, e.g. to add AppleSingle files to a ZIP archive as
AppleSingle, use `--no-from-any` to disable all file interpretation.

If `--mac-zip` is specified, files with resource forks or file type
attributes that are added to ZIP archives will be stored in a AppleDouble
format, as if the ADF header file were kept in a separate `__MACOSX`
directory.  This mode does not, however, enable special handling of files
in a `__MACOSX` directory on the filesystem.  (In other words, it enables
the special handling in the *destination* archive, not in the *source*.)

T/I/A/B files added to DOS disks will be treated as they would by `SAVE`,
`BSAVE`, or a sequential text file `WRITE`.  This limits I/A/B files
to 65533/65533/65531 bytes, and ends text files at the first zero byte.
With the `--raw` flag, the files are written as full sectors, so I/A/B
must start with the appropriate header bytes.

The most efficient way to add a large number of files is with a single
`add` command.  If the action needs to be performed with individual commands,
it may be beneficial to use `--fast-scan` to avoid the overhead of a
full-disk scan on every operation.

Options:
 - `--compress`, `--no-compress`
 - `--from-adf`, `--no-from-adf`
 - `--from-any`, `--no-from-any`
 - `--from-as`, `--no-from-as`
 - `--from-naps`, `--no-from-naps`
 - `--from-host`, `--no-from-host`
 - `--interactive`, `--no-interactive`
 - `--mac-zip`, `--no-mac-zip`
 - `--raw`
 - `--recurse`, `--no-recurse`
 - `--strip-paths`, `--no-strip-paths`

Examples:
 - `cp2 add archive.shk file1 file2 dir1`
 - `cp2 a disk.po:DIR1:DIR2 file1`

----
#### `catalog`|`v`

Generates a detailed listing of the contents of an archive.

Usage: `cp2 catalog [options] <ext-archive>`

The "type" and "auxtyp" columns will show either the ProDOS file type and
auxiliary type, or the HFS file type and creator, depending on which seems
to be the most relevant.  To see both, use the `--wide` option.

If `--mac-zip` is enabled, the `__MACOSX/.../._filename` entries will be
merged with the paired entry, and displayed as a single line.

The `--raw` option will change the display to show the "raw" file size.  This
is only meaningful on DOS 3.x disks, and primarily affects T/I/A/B files.

Options:
 - `--depth={shallow,subvol,max}`
 - `--mac-zip`, `--no-mac-zip`
 - `--raw`, `--no-raw`
 - `--show-notes`, `--no-show-notes`
 - `--wide`, `--no-wide`

Examples:
 - `cp2 catalog archive.shk`
 - `cp2 v --depth=shallow file.do`

----
#### `copy`|`cp`

Copies files and directories directly from one archive to another.

Usage: `cp2 copy [options] <src-ext-archive> [file-in-archive...] <dst-ext-archive>`

The source and destination ext-archive files must be different.  See
[Multiple Archive References](#multiple-archive-references).

If no file specification is provided, all files will be copied.  This is
independent of the recursion setting.

If a directory is named, the contents of that directory will be copied
recursively if recursion is enabled.  If recursion is not enabled,
directories are ignored.  Directories are not created as separate entries in
file archives, so empty directories will not appear in those.

The names of files may contain wildcards, and are matched
with case-insensitive comparisons.  See
[Filenames and Wildcards](#filenames-and-wildcards).  This is done for all
archives, regardless of underlying case sensitivity.  Copying "foo" and
"FOO" will cause a clash to be reported even if the destination archive
can hold both simultaneously.

Every file is effectively extracted and re-added.  While efforts are made
to preserve the original filename, file attributes, and structure, this is
not guaranteed.  If the source and destination have different capabilities,
e.g. different notions about what constitutes a legal filename, some
conversions may occur.  This is especially true when copying files to
or from DOS disk images.  On balance, the degree of preservation should
be higher than you would get by extracting files to the host filesystem
and adding them elsewhere (especially when copying files between two DOS
disk images).  When copying between two ZIP archives, using `--no-mac-zip`
may reduce the number of conversions.

The source and destination ext-archive specifiers may end with a directory
name, if the archives are disk images.  In both cases, the specified
directory must already exist.

Files in the destination will have the same name as the files in the source.
It's not possible to rename a file as part of the copy process.

All matching files are copied in a batch.  If one or more of the source
archive specifiers does not match, the copy process is cancelled.

The destination may not be a single-file archive like AppleSingle or gzip.

The `--raw-mode` option is not used here.  Files copied from DOS volumes
are always treated as "cooked" unless they are being copied to another
DOS volume, in which case the files are transferred "raw", with the sparse
structure preserved.

With `--mac-zip` enabled, copying an entry also copies the `__MACOSX` entry.

Options:
 - `--compress`, `--no-compress`
 - `--convert-dos-text`, `--no-convert-dos-text`
 - `--mac-zip`, `--no-mac-zip`
 - `--overwrite`, `--no-overwrite`
 - `--recurse`, `--no-recurse`
 - `--strip-paths`, `--no-strip-paths`

Examples:
 - `cp2 copy disk.po archive.shk`
 - `cp2 cp disk1.po FILE1 DIR1 DIR2:FILE3 disk2.po`
 - `cp2 cp disk1.po:DIR1 "*" disk2.woz:DIR2:SUBDIR3`

----
#### `copy-blocks`

Copies blocks from one disk image to another.  This will overwrite the
contents of the destination disk image.

Usage: `cp2 copy-blocks [options] <src-ext-archive> <dst-ext-archive> [<src-start> <dst-start> <count>]`

Data is copied from the source to the destination as a series of blocks.
If a range is not specified, the entire disk is copied.  The destination
file will not be extended, and so must be able to hold all of the blocks
being copied.  As a safety measure, if no range is specified, the source
and destination must be the same size.

This can be used to convert disks between different formats, e.g. DOS to
ProDOS order or .po to .woz.  The source and destination images must use
512-byte blocks, however, so this will not work for 13-sector disks.  (The
`copy-sectors` command is generally more appropriate for 5.25" disks.)

For unadorned 16-sector 5.25" disk images, the file sector order of the
source and destination images is determined from the filenames and file
contents.  The latter is only possible if the disk images have recognizable
filesystems.

If an unreadable or unwritable sector is encountered, a warning will be
printed on stdout, but the process will continue to completion.

When operating on partitions within a multi-partition image, the
`extract-partition` and `replace-partition` operations may be convenient.

Options: (none)

Examples:
 - `cp2 copy-blocks disk.po disk.do`
 - `cp2 copy-blocks disk.po multipart.po 0 3200 1600`

----
#### `copy-sectors`

Copies sectors from one disk image to another.  This will overwrite the
contents of the destination disk image.

Usage: `cp2 copy-sectors [options] <src-ext-archive> <dst-ext-archive>`

All data is copied from the source to the destination, reading and writing
tracks and sectors.  This can be used to convert disks between different
formats, e.g. DOS to ProDOS order or .po to .woz.  The destination must
have the same geometry (number of tracks and sectors) as the source.

For unadorned 16-sector 5.25" disk images, the file sector of source and
destination is determined from the filename and file contents.  The latter
is only possible if the disk image has a recognizable filesystem.

For nibble images, this does not transfer the low-level format of the
source, and does not alter the low-level format of the destination.
If an unreadable or unwritable sector is encountered, a warning will be
printed on stdout, but the process will continue to completion.  In some
circumstances this command can be used to copy data from a copy-protected
disk to an unprotected disk.

Setting `--sectors` is not required.  The sector count is configured
automatically.

Options: (none)

Examples:
 - `cp2 copy-sectors disk.nib disk.woz`

----
#### `create-disk-image`|`cdi`

Creates a new, empty disk image.

Usage: `cp2 create-disk-image [options] <new-archive> <size> [filesystem]`

The type of disk image created is determined by the filename, which must
end in a disk image extension (e.g. ".do", ".woz").

The size may be expressed in an integral number of bytes, kbytes (ends with
'k'), and so on.  See [#disk-image-sizes](Disk Image Sizes) for details.

Not all disk image formats support all sizes.  For example, ".nib" must be
35 tracks, but may be 13- or 16-sector.  ".d13" files are always 13-sector.

The filesystem is optional.  If no filesystem is specified, the image will
be left blank, filled with zeroes.  For a list of filesystems that can be
used to format a disk see [#filesystem-types](Filesystem Types).

Most filesystems can only be placed on disks with certain sizes.

DOS disks will be bootable unless `--no-reserve-boot` is specified.  ProDOS
and Pascal always have the Apple II boot blocks written, but the disks will
not actually boot without "PRODOS" or "SYSTEM.APPLE".  HFS boot blocks are
zeroed.  For CP/M, `--reserve-boot` will cause the first three tracks of
5.25" disks to be marked as reserved, but the formatter does not write an OS
image.

ProDOS and HFS disks are created with the volume name "NEW.DISK".  DOS disks
use volume #254.  These can be changed with the `move`/`rename` command.

Options:
 - `--sectors={13,16,32}`
 - `--reserve-boot`, `--no-reserve-boot`

Examples:
 - `cp2 create-disk-image newdisk.nib 35trk dos33`
 - `cp2 cdi floppy.woz 800KiB hfs`
 - `cp2 cdi bigdisk.2mg 32m ProDOS`

----
#### `create-file-archive`|`cfa`

Creates a new, empty file archive, to which files may be added.

Usage: `cp2 create-file-archive [options] <new-archive>`

The type of archive created is determined by the filename, which must end
with a multi-file archive filename extension (e.g. ".shk" or ".zip").

Options: (none)

Examples:
 - `cp2 create-file-archive new.zip`
 - `cp2 cfa Archive.shk`

----
#### `defrag`

Defragments a filesystem.

Usage: `cp2 defrag [options] <ext-archive>`

This is only implemented for Apple Pascal filesystems.  The process is the
same as is performed by the Filer's K(runch command.

Options: (none)

Examples:
 - `cp2 defrag pascal.po`

----
#### `delete`|`rm`

Deletes files or directories from an archive.

Usage: `cp2 delete [options] <ext-archive> <file-in-archive> [file-in-archive...]`

Directories may only be deleted if they are empty.  The "recursive" option
can help here.

For safety reasons, a file specification is required.  To delete all files,
`delete "*"` with recursion enabled.

The `--recurse` option is obeyed for file archives.  This allows removal
of a "directory" from an archive.

With `--mac-zip` enabled, removing an entry also removes the `__MACOSX` entry.

Options:
 - `--mac-zip`, `--no-mac-zip`
 - `--recurse`, `--no-recurse`

Examples:
 - `cp2 delete file.zip MYFILE1.TXT MYFILE2.TXT`
 - `cp2 delete dosdisk.do "*"`

----
#### `export`|`xp`

Extracts files, converting them to other formats.

Usage: `cp2 export [options] <ext-archive> <export-spec> [file-in-archive...]`

This behaves in a similar fashion to `extract` as far as file selection goes.
Filenames may include wildcards, directories will be descended into if the
`--recurse` option is set, and all files will be exported if no file
specification is provided.

Instead of extracting files as they are, however, the files are converted to
a different format.  The conversion performed is specified by `<export-spec>`,
which consists of a conversion name and zero or more options.  See
[Import and Export](#import-and-export) for details.

Exported files may have an additional filename extension added, e.g. graphics
extracted as PNG files will have ".png" appended.

If a file is not compatible with the export specification, the command will
fail.

Options:
 - `--overwrite`, `--no-overwrite`
 - `--raw`, `--no-raw`
 - `--recurse`, `--no-recurse`
 - `--strip-paths`, `--no-strip-paths`

Examples:
 - `cp2 export archive.shk hgr PIC1 PIC2`
 - `cp2 xp disk.po bas,hi=true "MYPROG*"`
 - `cp2 xp disk.po:VARIOUS best`

----
#### `extract`|`x`

Extracts files from an archive, making every effort to preserve the contents
as they are.

Usage: `cp2 extract [options] <ext-archive> [file-in-archive...]`

If no file specification is provided, all files will be extracted.  This is
independent of the recursion setting.

If a directory is named, the contents of that directory will be extracted
recursively if recursion is enabled.  If recursion is not enabled,
directories are ignored.

The names of files may contain wildcards, and are matched
with case-insensitive comparisons.  See
[Filenames and Wildcards](#filenames-and-wildcards).

DOS T/I/A/B files will be handled as they would by DOS for `LOAD`,
`BLOAD`, or a sequential text file `READ`.  This may result in partial
file extraction for certain 'B' files and for random-access text files.
Use the "--raw" flag to get all sectors of the file.

Options:
 - `--overwrite`, `--no-overwrite`
 - `--mac-zip`, `--no-mac-zip`
 - `--preserve=<mode>`
 - `--raw`
 - `--recurse`, `--no-recurse`
 - `--strip-paths`, `--no-strip-paths`

Examples:
 - `cp2 extract archive.shk FILE1 FILE2`
 - `cp2 x disk.po:DIR1:DIR2 AFILE DIR3:BFILE`

----
#### `extract-partition`|`expart`

Extracts a disk partition to a file.

Usage: `cp2 extract-partition [options] <ext-archive> <output-file>`

This can only be used with multi-partition images, like APF or CFFA.  A
single partition must be identified on the command line.

To extract an arbitrary range of blocks, see `copy-blocks`.

To replace the contents of a partition with the contents of a disk image, see
`replace-partition`.

Options:
 - `--overwrite`, `--no-overwrite`

Examples:
 - `cp2 extract-partition multi-part.hdv:2 mydisk.po`

----
#### `get-metadata`|`gm`

Gets metadata values from certain formats, e.g. 2IMG and WOZ, and displays
them.

Usage: `cp2 get-metadata [options] <ext-archive> [key]`

If no key is specified, all entries are displayed as key/value pairs.

The allowed formats for keys and values are different for every file format.
See the [Metadata](#metadata) section for details.

In some cases, additional detail may be provided in the output, e.g. an
enumerated value may be followed by an explanation.  The additional data can
be suppressed with the `--no-verbose` flag.

Options: (none)

Examples:
 - `cp2 get-metadata disk.woz info:creator`
 - `cp2 gm disk.2mg`

----
#### `help`

Shows a brief summary of commands and options.

Usage: `cp2 help [command]`

If a command is specified, detailed help for that specific command is shown.

Options: (none)

Examples:
 - `cp2 help`
 - `cp2 help cp`

----
#### `import`|`ip`

Adds files, converting them from other formats.

Usage: `cp2 import [options] <ext-archive> <import-spec> <file-or-dir> [file-or-dir...]`

This behaves similarly to `add` in most respects, but instead of adding
preserved "vintage" files, it converts host files into "vintage" formats
while adding them.  The conversion performed is specified by `<import-spec>`,
and is applied to all files.  See
[Import and Export](#import-and-export) for details.

The input file may be renamed.  For example, a converted text file will have
the ".txt" extension removed.  This behavior may be suppressed with the
`--no-strip-ext` option.

If a file is not compatible with the import specification, the command will
fail.

Options:
 - `--strip-ext`, `--no-strip-ext`

Examples:
 - `cp2 import archive.shk text "*.txt"`

----
#### `list`|`l`

Generates a simple list of the contents of an archive, one entry per line.

Usage: `cp2 list [options] <ext-archive>`

Control characters in filenames are converted to the Unicode "Control
Pictures" range, so there should be no confusion if CR, LF, or NUL are
part of the name.

Options:
 - `--mac-zip`, `--no-mac-zip`

Examples:
 - `cp2 list archive.shk`
 - `cp2 l file.do`

----
#### `mkdir`|`md`

Creates a new directory.  Only useful on disk images with hierarchical
filesystems, such as ProDOS and HFS.

Usage: `cp2 mkdir [options] <ext-archive> dir-name`

Missing directory components are added automatically, so you can create
"a/b/c" in one step.

Wildcards are not processed.  Filenames are adjusted to be valid on the
target filesystem.

If the directory already exists, the command does nothing and reports success.
The `--overwrite` option has no effect.

Options: (none)

Examples:
 - `cp2 mkdir prostuff.po DIR1/NEWDIR`
 - `cp2 md hfstuff.po NEWDIR1:NEWDIR2:NEWDIR3`

----
#### `move`|`rename`|`mv`

Renames and/or moves a file.

Usage: `cp2 rename [options] <ext-archive> <file-in-archive> [file-in-archive...] <new-name>`

For file archives, this can only be used to rename a single entry.
Recursion does not apply.  Pathname separator characters ('/' or ':')
in `<new-name>` are automatically converted to the appropriate character.

For disk images with hierarchical filesystems, this can move files between
directories, as well as rename them.

Files are specified as relative paths from the root directory, except for
the root directory itself, which is specified as ":" or "/".  The root
directory may be renamed (to change the volume name) but not moved.  This
can be used to change the volume number stored in the DOS VTOC, but will not
affect the volume numbers stored in the sector headers.

Moving a directory into a subdirectory of itself is not allowed.  Moving
files between file archives or filesystems (e.g. between partitions) is not
allowed.

If `<new-name>` exists and is a directory, the file is moved into the
directory, keeping its original name.  If multiple source files are
specified, all of them will be moved; in this case, `<new-name>` **must**
be an existing directory.

If `<new-name>` exists and is not a directory, the command will fail.
Setting the `--overwrite` will not change this behavior.

An entry with no name, such as in some AppleSingle files, can be specified
with "".

The command will fail if `<new-name>` is a partial path that includes
non-existent directories.  Wildcards are not evaluated for `<new-name>`.
The name will be adjusted for compatibility before it is applied.

This is not very useful for gzip archives.  While these optionally store a
filename internally, they are usually treated as having the current name
of the file without the ".gz" suffix.  (You can list the file with
`--no-skip-simple`, and perhaps `--depth=shallow`, to see the name stored
inside.)

Options:
 - `--mac-zip`, `--no-mac-zip`

Examples:
 - `cp2 move archive.zip FILE.OLD FILE.NEW`
 - `cp2 rename disk.po : DISKVOL`
 - `cp2 mv disk.po DIR1:FILE.TXT /`
 - `cp2 mv disk.po:DIR1 SUBDIR:FILE.TXT :`
 - `cp2 mv disk.po "DIR1:*.TXT" DIR3:DIR4 DIR1:DIR2`

----
#### `multi-disk-catalog`|`mdc`

Generates catalogs for multiple archive files.  Directories will be
recursively searched.

Usage: `cp2 multi-disk-catalog [options] <archive-or-dir> [archive-or-dir...]`

This generally behaves like the `catalog` command, but operates on
multiple files.

The `--classic` output roughly matches that of the MDC program included with
the original CiderPress.  It only displays the contents of disk images.
It can open disk images stored in ZIP or gzip archives, but only if they
have a single member, and cannot read .SDK files stored there.  WOZ images
are skipped.

Options:
 - `--classic`
 - `--depth={shallow,subvol,max}`
 - `--fast-scan`, `--no-fast-scan`
 - `--mac-zip`, `--no-mac-zip`
 - `--wide`, `--no-wide`

Examples:
 - `cp2 multi-disk-catalog file1.po file2.zip dir1`
 - `cp2 mdc --depth=max archive.shk`
 - `cp2 mdc --classic dir1 dir2`

----
#### `print`|`p`

Prints the contents of a file on stdout.

Usage: `cp2 print [options] <ext-archive> [file-in-archive...]`

The data fork of the specified files are printed to stdout.  Files are
assumed to hold ASCII text, except for those on DOS disks, which are
treated as high ASCII, and HFS disks, which are assumed to be Mac OS Roman.
End-of-line markers will be converted to the local format, and files on
DOS disks will have the high bit stripped.  Non-EOL control characters
will be converted to printable form.

This is only recommended for use with text files.

If multiple files are listed, they will be printed sequentially.  If no
file is specified, all files in the archive are printed.

Options:
 - `--mac-zip`, `--no-mac-zip`
 - `--recurse`, `--no-recurse`

Examples:
 - `cp2 print diskimage.po MYFILE.TXT`
 - `cp2 print diskimage.po "DIR1:*.TXT"`

----
#### `read-sector`|`rs`, `read-block`|`rb`, `read-block-cpm`|`rbc`

Reads a sector or block from a disk image.  The contents are displayed as
a hex dump.

Usage: `cp2 read-sector [options] <ext-archive> <track-num> <sector-num>`

Usage: `cp2 read-block [options] <ext-archive> <block-num>`

Usage: `cp2 read-block-cpm [options] <ext-archive> <block-num>`

For `read-sector`: only images of 5.25" floppy disks may be read as sectors.
Only whole tracks are supported (no half-tracks).  The sector number is
mapped with the DOS skew table, so the function works the same way an RWTS
call would.

For `read-block`: most disk images can be read as blocks, the notable
exception being 13-sector 5.25" disks.  On 5.25" disk images, the block is
read the way it would be from ProDOS, using the ProDOS/Pascal skew table.  The
`read-block-cpm` command works the same way, but with the CP/M skew table.

The ASCII portion of the hex dump is treated as high ASCII, with the high
bit stripped before display.

Options: (none)

Examples:
 - `cp2 read-sector file.nib 17 15`
 - `cp2 rb file.po 127`

----
#### `read-track`|`rt`

Reads a track of raw data from a nibble disk image.

Usage: `cp2 read-track [options] <ext-archive> <track-num>`

For a 5.25" disk, the track number is 0-39 with quarter-track increments
(.25, .5, .75).

For a 3.5" disk, the track number is 0-79, with the side specified as ".0"
or ".1".

The fractional portion may be specified with '.' or ',', regardless of locale.

The data will be read with an 8-bit latch, unless the `--no-latch` option
is used.  This only matters for bit-oriented image formats like WOZ.

Options:
 - `--latch`, `--no-latch`

Examples:
 - `cp2 read-track file.nib 17`
 - `cp2 rt file.woz 22.25`

----
#### `replace-partition`|`repart`

Replaces the contents of a disk partition with blocks from a file.

Usage: `cp2 replace-partition [options] <src-ext-archive> <dst-ext-archive>`

This can only be used with multi-partition images, like APF or CFFA.  The
source archive must be a plain disk image file, and the target archive must be
a single partition.

The source image must be the same size or smaller than the destination
partition.  If the source image is smaller, the operation will fail unless the
`--overwrite` option is given on the command line.

To copy an arbitrary range of blocks, see `copy-blocks`.

To extract the contents of a partition to a disk image, see
`extract-partition`.

Options:
 - `--overwrite`, `--no-overwrite`

Examples:
 - `cp2 extract-partition multi-part.hdv:2 mydisk.po`

----
#### `set-attr`|`sa`

Changes file attributes for a single record.

Usage: `cp2 set-attr [options] <ext-archive> <file-in-archive> [attrs...]`

`attrs` takes the form of name/value pairs.  If none are specified, the
current values are printed to the console in human-readable form.

All numeric values are entered as fixed-length hexadecimal numbers.  They may
be prefixed with "0x" or "$", though the latter is often a shell
metacharacter and may need to be escaped.

`type` sets the ProDOS file type.  It may be a type abbreviation, such as
"TXT", "BIN", or "LBR", or a two-digit hexadecimal value ($00-ff).
 - DOS file types are specified as ProDOS equivalents: T=TXT, I=INT, A=BAS,
   B=BIN, S=$F2, R=REL, AA=$F3, BB=$F4.
 - Pascal file types: untyped=NON, bad=BAD, code=PCD, text=PTX, info=$F3,
   data=PDA, graf=$F4, foto=FOT, securedir=$F5.

`aux` sets the ProDOS auxiliary type.  It must be a four-digit hexadecimal
value ($0000-ffff).

`hfstype` sets the HFS file type.  It may be a 4-character string, such as
"TEXT" or "cdev", or an 8-digit hexadecimal value.

`creator` sets the HFS creator type.  It may be a 4-character string,
such as "MACS" or "MSWD", or an 8-digit hexadecimal value.

`access` may be a two-digit hexadecimal value with the ProDOS access flags,
the word "locked", or the word "unlocked".

`cdate` and `mdate` are date/time strings for the creation and modification
timestamps, respectively.  Date/time strings will be parsed according
to local conventions.  It's best to use an unambiguous format like
[RFC 3339](https://www.ietf.org/rfc/rfc3339.txt)
(e.g. "yyyy-MM-ddTHH:mm:ssZ"), though other common formats like "dd-MMM-yyyy
HH:mm:ss" will also work.  Bear in mind that most formats store timestamps
in local time, without time zone information.

Not all filesystems and archive formats support all attributes, or all
possible values for a given attribute.  Attempting to set an unsupported
attribute will fail silently, or be adjusted to work as well as possible.

If the file is specified as ":" or "/", the root directory is selected.
This can be used to set modification dates on certain filesystems.

If MacZip is enabled, setting the attributes on the main file entry in a
ZIP archive will cause the "header" file to be updated if it exists.

When verbose mode is enabled, the updated record will be displayed.
The output will reflect the actual final state.

Editing of comments (for NuFX and Zip archives) is not currently supported.

Options:
 - `--mac-zip`, `--no-mac-zip`

Examples:
 - `cp2 set-attr archive.shk MYFILE type=TXT aux=0x0000 access=0xc3`
 - `cp2 sa archive.shk DIR1:FILE1.SHK type=0xe0 aux=0x8002 access=unlocked`
 - `cp2 sa prodisk.po FILE1.TXT "cdate=01-Jun-1977 09:05:25"`
 - `cp2 sa prodisk.po : mdate=1986-09-15T17:05:27Z`

----
#### `set-metadata`|`sm`

Sets metadata values for certain formats, e.g. 2IMG and WOZ.

Usage: `cp2 set-metadata [options] <ext-archive> <key> [value]`

The allowed formats for keys and values are different for every file format.
See the [Metadata](#metadata) section for details.

If no value is provided, the entry will be deleted.  This only works for
user-defined keys (currently only supported by WOZ).

Options: (none)

Examples:
 - `cp2 set-metadata disk.2mg write_protected true`
 - `cp2 sm disk.woz meta:language English`
 - `cp2 sm disk.2mg comment ""`

----
#### `test`

Tests all files in the archive by reading them.

Usage: `cp2 test [options] <ext-archive>`

A bad block scan will be performed on nibble disk images.

The `--fast-scan` option is disabled for this command, and `--show-notes`
is enabled, regardless of the provided options.

Options: (none)

Examples:
 - `cp2 test archive.shk`
 - `cp2 test disks.zip:file.woz`

----
#### `version`

Reports the version of the application and associated libraries on stdout.

Usage: `cp2 version`

Options: (none)

Examples:
 - `cp2 version`

----
#### `write-sector`|`ws`, `write-block`|`wb`, `write-block-cpm`|`wbc`

Writes a sector or block to a disk image.  The data is read from a hex dump.

Usage: `cp2 write-sector [options] <ext-archive> <track-num> <sector-num> <data-file>`

Usage: `cp2 write-block [options] <ext-archive> <block-num> <data-file>`

Usage: `cp2 write-block-cpm [options] <ext-archive> <block-num> <data-file>`

The format of the data file should match that generated by `read-sector`
or `read-block`.  The parser ignores any line starting with '#', and is
only interested in the hex digits.  The values in the address field and
the ASCII field are expected to be present, but ignored.

If `<data-file>` is "-", data is read from standard input.

Only images of 5.25" floppy disks may be written as sectors.  Only whole
tracks are supported (no half-tracks).  The sector number is mapped with the
DOS skew table, so the function works the same way an RWTS call would.

Most disk images can be written as blocks, the notable exception being
13-sector 5.25" disks.  On 5.25" disk images, the block is written the way it
would be from ProDOS, using the ProDOS/Pascal skew table.  If the
`write-block-cpm` command is given, the CP/M skew table is used instead.

Options: (none)

Examples:
 - `cp2 write-sector file.nib 17 15 sector-data`
 - `cp2 wb mydisk.po 147 - < block-data`

----


## Options ##

Any option may be provided in the arguments for any command, but each
command only pays attention to a specific set of them.

Two hyphens by themselves ("--") ends the option list.  Use this when you need
to reference a filename that begins with a hyphen.

Default options for specific commands and for the program in general can
be specified in a runcom file in the home directory.  The file will be
named `.cp2rc`, except on windows, where it's called `_cp2rc`.  If you're not
sure where to put it, the full pathname to the config file is shown in the
output of `cp2 version`.  See the sample file included in the distribution for
syntax and examples.

Short single-hyphen equivalents are provided for a few of the options:

 - `-f` = `--overwrite` (f = "force")
 - `-i` = `--interactive`
 - `-j` = `--strip-paths` (j = "junk paths")
 - `-p0` = `--preserve=none` (that's a zero)
 - `-pa` = `--preserve=as`
 - `-pd` = `--preserve=adf`
 - `-ph` = `--preserve=host`
 - `-pn` = `--preserve=naps`

These may be provided singly or in a single chunk, e.g. "-pnfj" is equivalent
to "-pn -f -j".

#### `--classic`

Argument for "mdc" command.  Generates output as close to the original MDC
as possible, for the benefit of side-by-side comparisons.

#### `--compress` (default), `--no-compress`

Enable or disable compression of files being added to a file archive.
ZIP uses Deflate, NuFX uses LZW/2, Binary II uses Squeeze.  If disabled,
files are stored without compression.

This option is ignored for gzip, which requires compression, and AppleSingle,
which does not support it.  The option has no effect when adding files to a
disk image.

This option affects the archive being modified as well as any containing
archives that need to be rewritten.

#### `--convert-dos-text` (default), `--no-convert-dos-text`

Converts text files that are copied to or from a DOS volume.  High ASCII
is added or removed.  End-of-line characters are not otherwise altered.

This option is currently only meaningful for the `copy` command, and is only
enabled when copying to a filesystem (i.e. not to a file archive).  To get the
same effect when adding or extracting files, use the `import` or `export`
command.

#### `--depth={shallow,subvol,max}` (default=shallow)

Archive descent depth, used when generating catalogs.
 - `shallow`: only the requested level is shown.
 - `subvol`: descend into sub-volumes, but don't open archives stored as
    files in the archive.
 - `max`: go nuts.

#### `--fast-scan`, `--no-fast-scan` (default)

When opening a disk image, the default behavior is to perform a detailed
scan of the filesystem.  While this provides an important safety check,
it's time-consuming and unnecessary in some circumstances.

Embedded volumes, such as DOS/ProDOS hybrids, may not be detected if the
full scan is skipped.

#### `--from-adf` (default), `--no-from-adf`
#### `--from-as` (default), `--no-from-as`
#### `--from-host` (default), `--no-host`
#### `--from-naps` (default), `--no-from-naps`
#### `--from-any`, `--no-from-any`

When adding files to a disk image or archive, the file attributes can be
determined in various ways, and an associated resource fork can be found
automatically.

If you want to add a file without reinterpretation, e.g. you want to add
an AppleSingle file as such rather than the contents of it, you would need
to disable the associated feature.

See [Resource Fork and Attribute Preservation](#resource-fork-and-attribute-preservation).

The `...-any` option enables or disables all of the other `--from-*` options.

#### `--interactive` (default), `--no-interactive`

Runs the command in interactive mode.  This is useful if, when adding or
extracting a file, a pre-existing file with the same name is found.  If
this flag is set, and `--overwrite` is not, the user will be prompted for
instructions.

#### `--latch` (default), `--no-latch`

When reading raw nibble data from a disk image, this controls whether the
data is passed through an 8-bit read latch.  This is primarily useful for
formats like WOZ that preserve long bytes, but some emulators will insert $00
bytes into ".nib" images to make the formatted track length come out right.

#### `--mac-zip` (default), `--no-mac-zip`

Enables or disables recognition of Mac OS ZIP tool encoding of resource
forks and file attributes.  See [Mac ZIP](#mac-zip).

#### `--reserve-boot` (default), `--no-reserve-boot`

If set, take additional steps to reserve the boot tracks.  For most
filesystems this has no effect.

For DOS 3.2/3.3, this will write a standard DOS image to tracks 0, 1,
and 2.  If the flag is not set, the tracks are zeroed out, and tracks 1
and 2 will be available for file storage.

DOS disks with unusual geometry (e.g. 80 tracks) require modified versions
of DOS that are not included in the disk format code.  The tracks will be
marked as in-use but not populated.

For CP/M on a 5.25" disk, the first three tracks will be marked as
reserved.

#### `--overwrite`, `--no-overwrite` (default)

If set, overwrite existing files without asking when adding or extracting.
It not set, the user will be queried for instructions if `--interactive` is
set, or the file will be skipped if not.

#### `--preserve={none,adf,as,host,naps}` (default=none)

When extracting files, various approaches can be used to preserve file
attributes and store resource forks.  Specifying one disables the others.
If `none` is given, files will be stored with no attempt at preserving
extended attributes, and resource forks will be ignored.

The mode may be `adf`, `as`, `host`, `naps`, or `none` (default).

See [Resource Fork and Attribute Preservation](#resource-fork-and-attribute-preservation).

#### `--raw`, `--no-raw` (default)

Use "raw" mode when adding and extracting files.  This is only useful for
DOS filesystems, and only affects T/I/A/B files.

When this is set, all files are treated as a collection of sectors.  Embedded
lengths are ignored when extracting files (and will actually be part of the
output).  All extracted files will be a multiple of 256 bytes.  When adding
files, any file whose length isn't a multiple of 256 bytes will be padded
with zeroes.

This will also cause the `catalog` commands to show the raw length instead of
the regular file length.

#### `--recurse` (default), `--no-recurse`

Applies an operation recursively.

When adding files, directories will be descended into.  If the flag is
not set, directories will be ignored.

When extracting files, the contents of directories will be extracted.
If the flag is not set, directories will be ignored.

When deleting a directory, this changes the operation from "remove the
directory entry" to "remove everything that lives in the directory, then
remove the directory entry".

#### `--sectors={13,16,32}` (default=16)

Specifies number of sectors per track, default 16.  Used when creating
disk images.

#### `--set-int=name:value`

This is a way to pass certain configuration options to lower-level code.
For example, `--set-int=dos-vtoc-track:0x15` would direct the DOS filesystem
code to look for the catalog on track 0x15 instead of 0x11.  You generally
won't need to use this.

#### `--show-log`, `--no-show-log` (default)

If enabled, a log of information useful for debugging will be shown after
the command has completed.

#### `--show-notes`, `--no-show-notes` (default)

Archives are checked for damage when opened.  Any problems or unusual
features are recorded in a "notes" list.  If this feature is active,
the notes will be displayed for certain commands (notably `catalog`).

#### `--skip-simple` (default), `--no-skip-simple`

Skip over "simple" layers when processing an ext-archive specification.
If set, a disk image compressed with gzip or stored in a NuFX .SDK file
would be treated as a disk image, rather than a gzip or NuFX archive.
The name of the file inside the gzip or .SDK archive does not need to be
included in the ext-archive path.

Disabling this is rarely useful.

#### `--strip-ext` (default), `--no-strip-ext`

When importing files, redundant file extensions are removed when this option
is enabled.  For example, doing a text import of "FOO.TXT" results in "FOO"
(with type TXT or TEXT).

#### `--strip-paths`, `--no-strip-paths` (default)

Enable or disable stripping of pathnames from files being added or extacted.
For example, adding "foo/bar/ack.bin" with stripping added will remove
"foo/bar/" from the filename and store the file as "ack.bin".

#### `--verbose`

Show additional information on stdout, such as per-file progress when
adding or extracting.

#### `--wide`, `--no-wide` (default)

Make the catalog display very wide, showing lots of detail.


## Special Arguments ##

Some arguments are limited to specific values.

### Disk Image Sizes ###

Used for commands like `create-disk-image`.

The size is an integer, possibly followed by a multiplier string,
e.g. "800K".  Supported multiplier strings are:

 - (none) - bytes
 - "K", "KB", "KiB" - kibibytes (1024 bytes)
 - "M", "MB", "MiB" - mebibytes (1024*1024 bytes)
 - "G", "GB", "GiB" - gibibytes (1024*1024*1024 bytes)
 - "T", "TB", "TiB" - tebibytes (1024*1024*1024*1024 bytes)
 - "BLK", "BLOCKS" - blocks (512 bytes)
 - "TRK", "TRACKS" - 5.25" disk tracks (4096 bytes, or 3328 bytes
    with `--sectors=13` flag)

The size indicates the amount of storage available to the filesystem,
not the size of the disk image itself.  A standard 5.25" disk would be
specified as "140KB" for both ".do" sector images and ".nib" nibble images.
(65535-block ProDOS volumes may be specified as 32MB.)

The size string is case-insensitive.

### Filename Extensions ###

Used for commands like `create-disk-image` and `create-file-archive`
to decide what sort of file to create.

 - ".do" - unadorned DOS-ordered disk sector image; for 16-sector 5.25" disks
 - ".d13" - unadorned DOS-ordered disk sector image; for 35-track
   13-sector 5.25" disks
 - ".po", ".iso", ".hdv", ".dc6" - unadorned ProDOS-ordered disk block image
 - ".nib" - unadorned 35-track 5.25" disk nibble image
 - ".woz" - WOZ format nibble image, for 5.25" (35- or 40-track)
   or 3.5" (SSDD or DSDD) disks
 - ".2mg", ".2img" - DOS-order 16-sector disks, or ProDOS-order blocks
 - ".dc", ".image" - DiskCopy 4.2 3.5" floppy (400KB, 720KB, 800KB, or 1440KB)
 - ".app" - Trackstar 5.25" (35- or 40-track) disk nibble image
 - ".sdk" - NuFX (ShrinkIt) disk archive
 - ".zip" - ZIP file archive
 - ".shk" - NuFX (ShrinkIt) file archive
 - ".bny", ".bqy" - Binary II file archive
 - [".ddd" - DDD / DDDPro]

Extensions not supported for file creation:

 - ".dsk" - unadorned ambiguously-ordered disk image
 - ".nb2" - variant of ".nib" that is no longer used
 - ".raw" - unadorned sector or unadorned nibble (ambiguous)
 - ".gz" - gzip file (just use "gzip" utility)
 - ".as" - AppleSingle file (extract a file as AppleSingle instead)
 - ".bin", ".macbin" - MacBinary files cannot be created
 - ".bxy", ".sea", ".bse" - ShrinkIt archive with a Binary II header,
   GSHK self-extracting header, or both (not needed)
 - ".acu" - AppleLink Conversion Utility archive creation is not supported

Creating 2IMG nibble images is not allowed, mainly because they offer very
little over .NIB, which is inferior to .WOZ.

### Filesystem Types ###

Filesystem type strings are used for commands like `create-disk-image`.
The filesystems that may be formatted onto a disk image are:

 - "cpm" - CP/M volume.  May be a 35-track 5.25" disk (140KB) or a 3.5"
   disk (800KB).  The first 3 tracks of a 5.25" disk can be reserved for an
   OS image by formatting the disk with the `--reserve-boot` flag.
 - "dos" - DOS 3.2 or 3.3, determined by the value of the `--sectors`
   option.  Disks must have 35, 40, 50, or 80 tracks, with 13, 16, or 32
   sectors.  Supported configurations:
   - 35 * 13 - standard 5.25" floppy, 13 sector (113KB)
   - 35 * 16 - standard 5.25" floppy, 16 sector (140KB)
   - 40 * 16 - 40-track 5.25" floppy (160KB)
   - 80 * 16 - 80-track 5.25" floppy (320KB) - not bootable
   - 50 * 16 - 50-track, 16 sector embedded volume (200KB)
   - 50 * 32 - 50-track, 32 sector embedded volume (400KB) - not bootable
 - "hfs" - Macintosh Hierarchical Filesystem volume.  Volumes may be fairly
   small or unreasonably large.  Here they must be at least 128KB but no
   more than 4GB.
 - "pascal" - Apple Pascal volume.  Recommended sizes are 140KB or 800KB,
   but anything from 6 blocks to 32MB is allowed.  Disks are limited to 77
   files, regardless of size.
 - "prodos" - ProDOS / SOS volume.  Volumes may be very small (5 blocks),
   and are limited to 65535 blocks (31.9MB).  Partitions are typically
   created as an even number of megabytes, so an "oversized" 65536-block
   image is also allowed.

Creation of Gutenberg, MFS, and RDOS disks is not supported.

### Import and Export ###

The export specification has the form `<conv-tag>[,name=value]...`.  The
`conv-tag` specifies the converter to use, and the optional name/value pairs
allow the behavior to be customized.

For example, to convert a hi-res screen image file to PNG format:
    `cp2 export archive.shk hgr MYPIC`

To do the same thing, but as a black & white image:
    `cp2 export archive.shk hgr,bw=true MYPIC`

Multiple options may be specified, separated by commas.  There are three kinds
of options: bool (true/false), int (decimal integer value), and multi
(multiple choice).  If an option is specified more than once, the last value
is used.  Unknown or invalid options are ignored.

Default values for options may be stored in the cp2rc config file.

Converters are available for code:
 - `ba3`: Business BASIC listing
   - `hi` (bool): false=plain text listing (default), true=add colorful
     syntax highlighting
   - `print` (bool): false=include raw control codes, true=make
     printable (default)
 - `bas`: Applesoft BASIC listing
   - `hi` (bool): false=plain text listing (default), true=add colorful
     syntax highlighting
   - `print` (bool): false=include raw control codes, true=make
     printable (default)
 - `int`: Integer BASIC listing
   - `hi` (bool): false=plain text listing (default), true=add colorful
     syntax highlighting
   - `print` (bool): false=include raw control codes, true=make
     printable (default)
 - `pcd` - Apple Pascal codefile
 - `ptx` - Apple Pascal textfile

Text documents:
 - `adb`: AppleWorks "Classic" Data Base
 - `asp`: AppleWorks "Classic" Spreadsheet
 - `awp`: AppleWorks "Classic" Word Processor
   - `mtext` (bool): true=display MouseText as Unicode near-equivalents
     (default), false=display MouseText as ASCII approximations
   - `embed` (bool): true=display embedded formatting codes (default)
 - `awgswp`: AppleWorks GS Word Processor
   - `embed` (bool): true=display embedded formatting codes (default)
 - `guten`: Gutenberg word processor
 - `magwin`: Magic Window word processor
 - `rtext`: convert DOS/ProDOS random-access text to cell-grid
   - `len` (int): specify record length; default value from aux type if
     available (if zero, file is converted as sequential text)
 - `teach`: Apple IIgs Teach Document

Graphics:
 - `dhgr`: Apple II double-hi-res screen
   - `conv` (multi): color conversion: `bw`, `latch`, `window`, or `simple`
 - `hgr`: Apple II hi-res screen
   - `bw` (bool): false=color (default), true=black & white
 - `macpaint`: MacPaint graphics document
 - `psclip`: Print Shop clip art, monochrome and color
   - `mult` (bool): true=multiplies pixels 2x horiz, 3x vert (default)
 - `shr`: Apple IIgs super hi-res screen ($C1/0000)
 - `shr3200`: Apple IIgs super hi-res 3200-color screen ($C1/0002)
 - `shrapf`: Apple IIgs super hi-res APF file ($C0/0002)
 - `shrdg`: Apple IIgs super hi-res DreamGrafix file ($C0/8005)
 - `shrpk`: Apple IIgs packed super hi-res screen ($C0/0001)
 - `shrpw`: Apple IIgs Paintworks super hi-res image ($C0/0000)

General:
 - `hex`: hex dump
   - `char` (multi): select character set for character dump portion
 - `text`: convert to text, changing end-of-line char to match the host system
   - `char` (multi): specify source character set
   - `print` (bool): false=include raw control codes, true=make
     printable (default)

The `char` character set options are `hiascii` for low/high ASCII (default),
`mor` for Mac OS Roman, and `latin` for ISO 8859-1.  Unprintable characters
(C0/C1 control codes) are displayed as a "middle dot" in hex dumps, and as
Control Picture glyphs for text files unless the printable-only option is
disabled.

The result of the conversion takes one of the following forms:
 - Images are exported as Portable Network Graphics (.PNG) image files.
 - Unstructured text files are output as plain text (.TXT), in UTF-8 without
   a byte-order mark.
 - Formatted documents are output in Rich Text Format (.RTF).
 - Spreadsheets and other cell-grid formats are output as Comma-Separated
   Value (.CSV) files, UTF-8 encoded.

Specifying the special value `best` as the converter tag will analyze the file
and choose the conversion that seems most appropriate.  It's not possible to
specify options with this, though defaults may be set in the config file.  See
the config file sample for a description of the syntax.

Formats that involve the resource fork will make use of it.  If the format
doesn't require the resource fork, e.g. a hex dump, the resource fork will be
ignored.  (If you want to get a hex dump of the resource fork, extract it and
use a tool like `xxd`.)


Import specifications work the same as export specifications, though there is
no `best` conversion.  All attribute preservation parsing options (NAPS,
AppleSingle, etc.) will be disabled, because the input files are expected to
be host files.

The available converters are:
 - `bas`: convert Applesoft BASIC listing back to a tokenized program (only
   works on .txt output, not .rtf output)
 - `text`: convert host text file, replacing end-of-line markers and
   converting characters
   - `inchar` (multi): define input (host) character set
   - `outchar` (multi): define output (archive) character set

The `inchar` setting may be `utf8` (default), `latin` for ISO 8859-1, or
`1252` for Windows Code Page 1252.  Use `utf8` for plain ASCII; it will also
work for UTF-16.

The `outchar` setting may be `ascii` (default), `hiascii` (DOS characters with
their high bits set), or `mor` for Mac OS Roman.  `ascii` is automatically
switched to `hiascii` when importing files to a DOS disk.


## Resource Fork and Attribute Preservation ##

Files created on DOS, ProDOS, and HFS filesystems have metadata stored in the
filesystem that will be lost when files are extracted unless care is taken to
preserve it.  Various approaches are supported, selectable with the
`--preserve=` option.

Some of the preservation methods can lead to situations where multiple files
are merged into one before they are added.  This is required to recombine
resource forks, but occasionally causes trouble.  For example "GSHK" and
"GSHK#b3db07" are both data forks of the same file.  It is best to avoid
having files with identical names and different preservation methods in the
same directory.

For most methods, both forks must be specified on the command line.  For
example, to add a NAPS-preserved file with data and resource forks, you must
include both files in the argument list.  AppleDouble is an exception:
because the AppleDouble header files are "hidden" from some shells (the names
start with '.'), they won't automatically be included in wildcards, so they
are explicitly searched for.  For "host" preservation, the resource fork is a
part of the same file, and it will be included automatically.

When files are added without file type information, default types are
provided.  For ProDOS this is NON/$0000, for HFS 'BINA'/'CPII' is used
(generic binary).

#### None (default) ####

Resource forks, file types, and access flags are discarded.  File dates will
be preserved if there are equivalents in the filesystem.

#### AS (AppleSingle) ####

All files are stored in AppleSingle format.  This creates a single file
for both data and resource forks.  The ".as" file extension is applied
when extracting, and required when adding.

AppleSingle preserves the original filename, file types, creation and
modification dates, and access flags.  The stored filename, adjusted for
host filesystem limitations, is used when extracting.

#### ADF (AppleDouble) ####

All files are stored in AppleDouble format.  Data forks are stored in
a plain file with the original name, while the resource fork and file
attributes are stored in a file that starts with "._".  The second file
is created even for files without resource forks, because that's where
the file attributes are stored.

AppleDouble preserves file types, creation and modifications dates,
and access flags.  The filename used for the data fork will be used when
extracting.

#### Host-Specific ####

Mac OS systems using HFS or HFS+ can store resource forks and file types
natively.  Resource forks use the "..namedfork/rsrc" convention to hold the
resources in the same file as the data fork.  File types are stored in the
"com.apple.FinderInfo" extended attribute.

MacOS hosts with HFS/HFS+ can store the HFS file types, creation and
modification dates, and some access flags.

#### NAPS (NuLib2 Attribute Preservation String) ####

The filetype, auxtype, and fork identifier will be encoded in a "hashtag"
appended to the filename of extracted files, e.g. "FILE.BIN#062000" for
type=$06 aux=$2000.  When added back to an archive, the string is stripped
off, and the data used to set the filetype and auxtype, and determine which
fork the data is copied into.  Resource forks are stored in separate files,
using the same filename but with an 'r' added to the string.

Illegal characters in the filename ('/', '*', and so on) will be replaced
with an escape sequence.  The escape sequence is '%' followed by the
two-digit hexadecimal representation of the character (all illegal characters
are in the ASCII range).  These will be un-escaped when the file is added
with NAPS enabled, with appropriate adjustments made for the disk image or
archive that the file is added to.  The null byte ("%00") has a special
meaning, and is stripped away.

Only the filename is escaped, not the full pathname.  This allows
NAPS and non-NAPS file to be stored in the same directory.  Otherwise,
"sub%3fdir/file%3a.txt#040000" and "sub%3fdir/file.bin" would be added
to two different directories, because NAPS escaping is only triggered for
NAPS files.  (We could put "#0f0000" on all directories, but that's ugly.)

NuLib2 extracts .SDK disk images to unadorned ProDOS-ordered image files,
using a NAPS name with 'i' on the end.  These are not supported, and will
be ignored when adding files.

NAPS preserves the key file attributes, but can't restore them all.  The
ProDOS or HFS file types is recovered from the filename, but if both were
set then one will be lost.  The modification date from the file will be used,
and the read-only flag will be used to set the access flags.

### Finding Resource Forks ###

When adding files, the process for finding associated resource forks is:
 - If ADF is enabled, and a file with the same name prefixed by `._` is
   found, it will be checked to see if it's AppleDouble.  If so, the
   contents are used as the resource fork, and will be merged with the
   data fork file when found.
 - Else, if AS is enabled, and the file has the extension ".as", it will
   be checked to see if it's AppleSingle.  If so, the contents are used,
   and both forks are obtained from the file.
 - Else, if NAPS is enabled and the file has a "hashtag" extension, the
   extension will be stripped and the file will be treated as data or
   resource.  When the other part of the file is found, the forks will
   be combined.
 - Else, if Host is enabled, every data file open will be paired with a
   check for a resource fork.

Mixing and matching is not advisable.  For example, when adding a file called
"FOO#062000", if NAPS is enabled then no attempt to find a "..namedfork/rsrc"
entry will be made.

### Access Flags ###

The access flags shown in the `catalog` output are based on the ProDOS
definition:
```
  $01 r - read allowed
  $02 w - write allowed
  $04 i - invisible / hidden
  $08 (reserved)
  $10 (reserved)
  $20 b - backup needed
  $40 n - rename allowed
  $80 d - delete allowed
```
Many filesystems and archive formats only have a notion of "locked" vs.
"unlocked", where unlocked allows read/write/rename/delete, and "locked"
only allows reading.  When converting between formats, the value will be set
based on the value of the "write" flag.


## Metadata ##

Certain formats have metadata that can be viewed and, in some cases, edited.
The set of possible values associated with certain keys may be restricted.
Keys are case-sensitive.  The "acc" column in the tables below indicates
whether the key is read-only or read/write.

Boolean values may be set to "true" or "false".

### DiskCopy 4.2 ###

name                     | acc | description
------------------------ | --- | -----------
description              | r/w | Mac OS Roman text string, 63 characters max

### Trackstar ###

name                     | acc | description
------------------------ | --- | -----------
description              | r/w | ASCII text string, 46 characters max

### 2IMG ###

name                     | acc | description
------------------------ | --- | -----------
creator                  | ro  | four-character creator tag
format                   | ro  | image format (0=DOS order, 1=ProDOS order, 2=nibble)
write_protected          | r/w | boolean; "true" marks disk as write-protected for emulators
volume_number            | r/w | volume number (0-254), for DOS disks
comment                  | r/w | string; ASCII only

The volume number is optional, so deleting `volume_number` will remove the
stored value.  Emulators are expected to use the default (254) in that case.

### WOZ ###

For a full description of the meanings of the keys and values, see
https://applesaucefdc.com/woz/reference2/.  The names of keys stored in
the INFO chunk must be prefixed with "info:", and those stored in the
(optional) META chunk must be prefixed with "meta:".

INFO chunk keys:

name                     | acc | description
------------------------ | --- | -----------
info:version             | ro  | numeric
info:disk_type           | ro  | numeric
info:write_protected     | r/w | boolean; "true" marks disk as write-protected for emulators
info:synchronized        | ro  | boolean
info:cleaned             | ro  | boolean
info:creator             | ro  | string
info:disk_sides          | ro  | numeric
info:boot_sector_format  | r/w | numeric
info:optimal_bit_timing  | ro  | numeric
info:compatible_hardware | r/w | 16-bit collection of bit flags
info:required_ram        | r/w | 16-bit value, in KiB

META chunk "standard" keys:

name                     | acc | description
------------------------ | --- | -----------
meta:title               | r/w | string; product title
meta:subtitle            | r/w | string; product subtitle
meta:publisher           | r/w | string; product publisher
meta:developer           | r/w | string; product developer
meta:copyright           | r/w | string; copyright
meta:version             | r/w | string; version
meta:language            | r/w | language name, from table
meta:requires_ram        | r/w | RAM size, from table
meta:requires_machine    | r/w | pipe-delimited list, from table
meta:notes               | r/w | string; arbitrary notes
meta:side                | r/w | side label string, in a specific format
meta:side_name           | r/w | string; name of disk side
meta:contributor         | r/w | string; disk imager
meta:image_date          | r/w | RFC3339 date of imaging

The META chunk can hold arbitrary additional keys.  Values are encoded with
UTF-8, and may not include pipe, linefeed, or tab characters (except for
`requires_machine`, which can include '|').  The format for keys isn't
well defined in the documentation, so they are restricted to alphanumeric
characters and the underscore ('_').

Setting a "meta:" key in an image without a META chunk will cause a new
META chunk to be added.  All fields will be blank except for `image_date`.


## Appendix ##

Additional information, not required reading.

### Mac ZIP ###

The Mac Finder's "compress file" feature will store one or more files in a ZIP
archive, storing them in AppleDouble format if they have resource forks or
extended attributes.  The header file's name is modified in two ways, adding
`__MACOSX/` to the start of the pathname and `._` to the start of the
filename.  This convention can result in the AppleDouble header files being
extracted to a parallel directory hierarchy by tools that don't have special
handling for the layout.

The `--mac-zip` option is enabled by default.  This causes paired entries to
be handled as a single entry that can have two forks and extended file
attributes such as HFS file type and creator.  Disabling the option allows
the entries to be managed as they would by a standard ZIP utility.

### AppleWorks Filename Formatting ###

ProDOS filenames are limited to 15 upper-case letters, numbers, and '.'.
Files created by AppleWorks can appear within the program to have lower-case
letters and spaces, because the program uses the ProDOS auxtype field as a set
of flags that specify whether a character is upper- or lower-case.  Spaces are
encoded as "lower-case" periods ('.').

While this looks nice, handling it in a command-line utility is problematic,
because the standard case-insensitive comparison function won't treat ' ' and
'.' as equivalent.  If AppleWorks names a file "My File", we need to be able
to give the filename on the command line as "My File" or "My.File" (or
even "mY.filE").  A special conversion could be applied for ProDOS disks,
but if the file is extacted or copied to a filesystem or archive format that
allows spaces, we could have "My.File" and "My File" on the same volume.  Now
the file *must* be referenced as "My.File" or you'll get the wrong one, and we
can't apply the AppleWorks transformation on anything but ProDOS because it
will potentially be ambiguous.

A major sticking point is that we want to be able to "catalog" or "list" an
archive and use the output as command-line arguments, so we need to output a
name with no ambiguity.  Having a context-sensitive filename makes things
more complicated while providing little benefit.  Consequently, cp2 does not
perform the AppleWorks-specific case conversion.  Instead, the output matches
what ProDOS or GS/OS would show in a directory listing.  (We *could* safely do
the lower-case conversion if we left periods alone, but currently don't.)

Lower case letters on ProDOS are still possible, because GS/OS defined a
filesystem tweak that repurposes a rarely-used 16-bit field as a set of
lower-case flags.  GS/OS does not allow spaces in ProDOS names, however.
This extension is fully supported.

### "TO DO" List ###

Some ideas for the future:
 - Add creation tool for multi-part images (APM, CFFA, etc).
 - Add whole-partition manipulation, e.g. add-partition / extract-partition.
 - Add tools to assist physical media access.
 - Add file "fingerprint" feature: print CRC-32 or other hash for data/rsrc
   next to each filename.
 - Multi-archive "grep" (same transformation as "print", but does text search).
   Could support modifiers, e.g. search by file type or mod date range.
 - Non-archive file utilities: EOL / high ASCII converter, sciibin, etc.
 - Support half/quarter tracks and 524-byte sectors in the read/write sector
   commands.
 - Provide a way to set the volume number when creating a new 5.25" disk image.
 - Add track/sector ranges to copy-sectors command.
 - Add options to configure the character set in sector edit hex dumps.
 - Add sector skew order option for sector edit commands.
 - Add resource fork manipulation routines (`rez`/`derez` commands).
 - Support editing of ZIP/NuFX file comments in set-attr.
 - Add `get-attr` to get file attributes in machine-readable form.
 - Add a better way to set access flags in `set-attr`, e.g. by letter.
 - Add `show-vol-bitmap` to display free/in-use blocks.
 - Allow `test` to descend into the archive (use `--depth` option).
 - Add command to zero out unused blocks on disk images, and perhaps the
   names of deleted files, to improve compression / security ("wipe",
   "clean", "scrub", ?)

Additional short options to consider:
 - `-v` for `--verbose`?  (Not needed, verbose is default)
 - `-r` for `--recurse`?  (Not needed, recurse is default)
 - `-w` for `--wide`?  (Rarely used)
