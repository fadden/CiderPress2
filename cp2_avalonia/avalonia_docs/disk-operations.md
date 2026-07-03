# Disk Operations

These whole-disk operations are available when a **disk image** or **partition** is
selected in the Archive Contents tree, not when a filesystem or file archive is
active. Reach them from the **Actions** menu or the **Disk / Partition Utilities**
buttons in the center Info panel. (Block/sector editing has its
[own page](sector-editing.md).)

---

## Save As Disk Image

Save the contents of a disk image or partition to a **new** disk image, optionally in a
different format, a quick way to convert between formats.

Select the image or partition, click **Save As Disk Image** (or use the Actions menu),
pick a format (options unsuitable for the disk's size are disabled), click **Save**, and
choose a name and location.

Conversion caveats:

- **Block/sector → nibble:** generates standard low-level formatting; behaves like the
  original.
- **Nibble → block/sector:** discards low-level formatting and may partially fail if the
  source has errors or copy protection. Read errors are reported, but some losses (like
  the volume numbers embedded in 5.25" sector address headers) are *not* called out.
- **Nibble → different nibble format:** still loses data, because the transfer is always
  done at the block/sector level.

---

## Replace Partition Contents

Available for **partitions** only. Overwrites a partition with the contents of a disk
image. The image's filesystem must be the same size as the partition or smaller.

Select the partition, click **Replace Partition Contents** (or use the Actions menu),
and choose the source image. A window shows the geometry of source and destination and
asks for confirmation; if the two aren't compatible you can't continue. Click **Copy**
to proceed or **Cancel** to abort.

> **This overwrites the entire partition.** The previous contents are destroyed, not
> merged.

---

## Scan For Bad Blocks

Available for **nibble-format** images (`.nib`, `.woz`). Scans for unreadable blocks or
sectors and reports the failures. It is **non-destructive** and makes no attempt to
repair anything. It does not check for filesystem problems; those are scanned
automatically whenever a disk image is opened.

---

## Defragment Filesystem

Available only for filesystems with serious fragmentation, and currently implemented
only for **Apple Pascal**. Technically a filesystem utility, but it requires all file
access to be disabled while it runs.

Select the filesystem and choose **Actions → Defragment Filesystem**; it runs
immediately. For safety, you can't defragment a filesystem that has any errors or
irregularities.
 
 