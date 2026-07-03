# Creating Archives

CiderPress II can create empty file archives and new disk images. Both are also
reachable from the launch screen buttons.

---

## Creating a File Archive

Choose **File → New File Archive**, pick a format from the list, click **OK**, then
choose a name and location. The empty archive opens automatically.

Notes:

- Some utilities are confused by completely empty archives. Binary II archives have no
  file header, so an empty one is zero bytes long.
- You can't create **AppleSingle**, **AppleDouble**, or **gzip** archives here; they're
  designed to hold a single file. To produce AppleSingle/AppleDouble, **extract** a file
  with that preservation mode selected (see
  [Add/Extract/Import/Export](add-extract-import-export.md)).

---

## Creating a Disk Image

Choose **File → New Disk Image** (Ctrl/⌘+N). The dialog walks you through three
choices.

### 1. Disk size

Pick a standard floppy size, or specify a custom size in blocks.

### 2. Filesystem

Only filesystems compatible with the chosen size are offered. Enter a volume name or
number if the filesystem uses one. For DOS 3.x and CP/M floppies you can reserve space
for the operating system image (for DOS 3.2/3.3, a copy of the OS is installed).

### 3. Disk image format

Only formats compatible with the chosen size are offered:

| Format | Notes |
|---|---|
| **Simple block image** (`.iso`/`.hdv`) | Disk as 512-byte blocks. Common for 3.5" disks, hard drives, and other block media. |
| **Unadorned ProDOS-order** (`.po`) | Same as block image; the `.po` extension is recognized by many emulators. |
| **Unadorned DOS-order** (`.do`/`.d13`) | Apple II 5.25" floppy as 256-byte sectors. The most common 5.25" format (often the ambiguous `.dsk`). `.do` = 16-sector, `.d13` = 13-sector. |
| **2IMG** (`.2mg`) | Like `.do`/`.po`/`.nib` plus a header so receivers can identify the contents unambiguously. Widely supported by emulators. Here, ProDOS-ordered only. |
| **ShrinkIt** (`.sdk`) | Compressed 5.25"/3.5" image. Small and unpackable to physical disk via ShrinkIt, but not all emulators support it. |
| **DiskCopy 4.2** (`.image`) | Apple's 3.5" Macintosh imaging format. Common on the Mac side, less so for Apple II. |
| **WOZ** (`.woz`) | Faithful low-level capture of 5.25"/3.5" floppies. Mainly for digitizing physical media. Supported by most modern emulators. |
| **Nibble** (`.nib`) | Low-level 5.25" representation: more detail than block/sector, less than WOZ. |
| **Trackstar** (`.app`) | For the Trackstar Apple II emulator card. Use only when targeting Trackstar. |

Make your selections, click **Create**, choose a name and location, and the new image
opens automatically. 

