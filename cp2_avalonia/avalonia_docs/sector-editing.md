# Sector & Block Editing

Disk images and partitions can be edited at the raw level, as **256-byte sectors** or
**512-byte blocks**, using the built-in hex editor.

- **Sector editing** is available when the disk geometry can be expressed as tracks
  with a fixed number of sectors per track.
- **Block editing** is available when the disk can be viewed as a series of 512-byte
  blocks.

So a 13-sector 5.25" image can only be sector-edited, a 32 MB hard drive can only be
block-edited, and a 16-sector 5.25" disk can go either way.

> **Why sector order matters.** Apple II operating systems interleave 16-sector disks
> using a software skew, so sectors aren't stored in simple sequential order. DOS,
> ProDOS/Pascal, and CP/M each use a different scheme. When reading from a nibble image,
> selecting the correct translation table matters. Block images are simpler and store
> blocks sequentially.

---

## Opening the Editor

Select the disk image or partition, then use the **Disk / Partition Utilities** buttons
in the center panel or the **Actions** menu:

- **Edit Sectors**: track/sector addressing, a 256-byte data area, DOS 3.3 skew.
- **Edit Blocks**: block addressing, a 512-byte data area, ProDOS order.

> **Change from earlier versions:** the old separate *Edit Blocks (CP/M)* menu item is
> gone. Instead, when you open the editor in **block mode**, a **Block order** dropdown
> appears in the *Advanced* panel offering **ProDOS** and **CP/M** ordering. The CP/M
> option is enabled only for 16-sector floppy images where it's meaningful; otherwise
> it's shown disabled. Switching block order re-reads the current block immediately so
> you can compare the two views without reopening the dialog. (If you have unsaved
> edits, you'll be asked to confirm before switching.)

---

## Navigating

- Enter track/sector or block numbers in **decimal or hex** (prefix hex with `$` or
  `0x`). The valid range is shown.
- Click **Read** to load that location, or **Write** to store the on-screen data. You
  may write to a location other than where you read, but you'll be asked to confirm.
- **Prev** / **Next** step backward and forward by one block or sector.

---

## Editing Data

Click a byte in the hex grid and type two hex digits (`0`–`9`, `a`–`f`). Editing the
text column directly isn't supported.

- For 16-sector 5.25" images, change the interleave with the **Sector skew** dropdown.
- For nibble images, the **Sector format** (codec) is shown.
- The **Text Conversion** options change how the text column is rendered:
  **High/low ASCII**, **Mac OS Roman**, or **ISO Latin-1**.
- **Copy to Clipboard** produces a text copy of the current hex dump for sharing.

---

## Effect on the Filesystem

Editing raw data can change (or break) the filesystem. As soon as you begin making
changes, the filesystems and partitions inside that image are **removed** from the
Archive Contents tree. When you close the editor the disk is **re-scanned** and the
filesystem is re-added if it can still be recognized. 

