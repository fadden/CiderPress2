# Notes on Hybrid Disk Formats #

This document discusses disk images with multiple operating systems.  While multi-partition
formats typically have a series of well-defined discrete containers, hybrid disks have filesystem
regions that overlap.  This can make them more difficult to detect.

## Primary References ##

- Some documentation, mostly reverse-engineering

## DOS MASTER ##

DOS MASTER, by Glen Bredon, allows DOS 3.3 volumes to be embedded in a ProDOS filesystem.  A
common use was to put multiple DOS volumes on an 800KB floppy.  The program allocates a
contiguous region of blocks, usually at the end of the disk, and marks them as "in use" without
creating an associated file for them.  Even though the DOS regions are accessible by ProDOS,
they won't be overwritten because the blocks aren't available to files.

The individual DOS volumes may be 140KB (35 tracks), 160KB (40 tracks), 200KB (50 tracks), or
400KB (50 tracks, 32 sectors each).  If the volumes completely fill the target disk, the first
7 blocks will be reserved for the ProDOS boot blocks and volume directory, so the disk will
still be recognized as a valid ProDOS volume (albeit with no free sectors).  The installer
also gives the option of reserving an additional 28KB (7 tracks) for "PRODOS" and the "DOS.3.3"
executable, allowing the disk to be booted directly into DOS 3.3.

The presence of DOS MASTER volumes can be identified by looking for contiguous in-use blocks that
aren't part of any file on a ProDOS disk.  Once found, the size of the region provides a
reasonable guess at the number and size of the DOS volumes present.  Scanning for the DOS
catalog track within each provides confirmation.  An alternative approach is to check for DOS
tracks in various configurations, but this has a higher risk of false-positives, especially if
a set of DOS regions was created, removed, and re-created.

## 140KB DOS Hybrids ##

It's possible to create a "hybrid" disk that has both the DOS 3.3 filesystem and another
filesystem, so long as the other begins on track 0.  ProDOS, UCSD Pascal, and CP/M filesystems
all qualify.  This works because the "starting point" of a DOS filesystem is the VTOC on
track 17 sector 0.  By marking tracks 0 through 16 as in-use but not associated with a file,
the other operating system can avoid being trampled by DOS by marking tracks 17+ as in-use int
the VTOC.

The other operating system can be configured in one of two ways.  For ProDOS, which has a
blocks-in-use bitmap, the volume can be configured to span the entire volume, with the blocks
on the second half of the disk marked as being in use.  For UCSD Pascal, which uses the file
entries to determine block usage, it's necessary to declare the volume size as shorter than
the full length of the disk.

The DOS MASTER distribution disk provides an interesting case: tracks 17 through 21 are DOS 3.3,
but the rest of the disk (tracks 0-16 and 22-34) is ProDOS.  This is very different from a
DOS MASTER volume, which has a complete DOS disk stored inside a ProDOS filesystem, and a little
different from other hybrids, which tend to split the disk in half.  (The DOS catalog track is
only one sector long, which makes recognition tricky.)

DOS hybrids can be detected by examining the VTOC for in-use blocks that aren't part of any file.
This isn't perfect because the first three tracks of most DOS disks are already in this state
because they hold the DOS boot image, but if the no-file in-use area extends to track 3 and beyond
then it's worth scanning for other known operating systems.  Another approach is simply to test
each disk for every known operating system, and accept all that appear.

One utility for creating DOS/ProDOS hybrids is called HYBRID.CREATE, part of the "Extra K"
utilities sold by Beagle Bros.

Another is "Doubleboot", written by Ken Manly and published by MicroSPARC, Inc.  It creates a
hybrid DOS/ProDOS disk with a custom boot loader that allows either OS to be booted.  The number
of tracks reserved for DOS can be adjusted, and the catalog track length is cut in half, with
half the blocks being available to ProDOS (which is tricky due to the software sector skewing).
https://www.applefritter.com/appleii-box/APPLE2/ProDOSandDOS33DoubleBoot/DoubleBoot.pdf has a
listing that can be typed in.
