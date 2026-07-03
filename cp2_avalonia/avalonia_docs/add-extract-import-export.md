# Add / Extract / Import / Export

These four operations move files between your host system and the open archive.
**Add/Extract** transfer files faithfully; **Import/Export** convert them. See
[Overview & Concepts](overview.md#addextract-vs-importexport) for the distinction.

All four share the option controls in the **Settings panel** on the right side of the
main window, so the same choices apply whether you start an operation from a menu,
the clipboard, or a drag-and-drop.

---

## Attribute Preservation Schemes

Files in disk images and archives carry attributes: file types, dates, access flags,
and sometimes a **resource fork** (a second stream of data, used on the Apple IIgs and
Macintosh). Host filesystems can't always represent these directly (for example, HFS
allows a file named `Face/Off`, which Windows, Linux, and modern macOS do not). When
extracting, CiderPress II can encode these attributes so nothing is lost, and decode
them again when adding.

Five strategies are supported:

- **None**: discard resource forks, file types, and access flags on extract. File
  dates are kept if the host filesystem has an equivalent (most record modification
  dates; fewer store creation dates).
- **AppleSingle (AS)**: store each entry as a single `.as` file containing the data
  fork, resource fork, original name, and all attributes.
- **AppleDouble (ADF)**: store each entry as a pair: the data fork under the original
  name, plus a header file (name starting with `._`) holding the resource fork and
  attributes. Convenient because tools that only care about the data fork can read it
  normally. The header file is structurally identical to AppleSingle.
- **NAPS (NuLib2 Attribute Preservation Strings)**: encode the file and auxiliary
  type in the filename by appending a string like `#062000` (invalid characters become
  `%` escapes); resource forks go in a separate file whose suffix ends in `r`.
  Convenient and widely used, but it can lose file dates and most access flags.
- **Host-specific**: store forks and extended attributes using native host
  filesystem features. Only meaningful on macOS, and currently offered only through the
  command-line tool, not this GUI.

---

## The Settings Panel

### Add / Import Options

| Option | Effect |
|---|---|
| **Recurse Into Directories** | Descend into selected subfolders. |
| **Use Compression** | Compress added files (only for archives that support it). |
| **Strip Paths** | Drop partial paths from files as they're added. |
| **Raw (for DOS 3.x)** | Open data forks in raw mode. Only meaningful for DOS 3.2/3.3, and only for data that was generated/extracted as raw. |
| **Preservation Handling** (AppleSingle / AppleDouble / NAPS) | Which preservation schemes to *recognize and recombine* when adding. Normally leave all three checked; uncheck one to add such a file literally (e.g. add a `.as` file as-is instead of unpacking it). |
| **Strip Redundant Extensions** | When importing, remove extraneous extensions (e.g. set type TXT and drop the `.txt`). |
| **Conversion mode** | Which importer to apply. Importers are *not* auto-selected; you must choose one. |

### Extract / Export Options

| Option | Effect |
|---|---|
| **Strip Paths** | Extract without subdirectory names (matters for ProDOS/HFS and archives that store full paths). |
| **Add Filename Ext to Exports** | Append an extension to exported files (`.txt`, `.png`, …). Turn off when extracting source that already has meaningful extensions like `.c` or `.asm`. |
| **Raw (for DOS 3.x)** | Open data forks in raw mode to preserve full DOS 3.2/3.3 contents. |
| **Preservation Mode** (None / AppleSingle / AppleDouble / NAPS) | Single choice of scheme to use when extracting. |
| **Conversion mode** (Best / specific) | Which exporter to apply. **Best** lets CiderPress II choose automatically; or pick a specific converter from the dropdown. |

> Default parameters for importers and exporters (text character set, hi-res color
> mode, and so on) are configured through **Edit → Settings → Import/Export
> Conversion**. See [Settings](settings.md).

Collapse the panel with **Show/Hide Settings** to give the file list more room.

---

## Adding and Extracting

**To add:** choose **Actions → Add Files…**, pick the files and folders, and confirm.
They're added to the folder currently selected in the Directory tree.

**To extract:** select the files/folders in the file list, choose **Actions → Extract
Files…**, navigate to the destination folder, and confirm. If a file already exists,
you're prompted to overwrite or skip that file.

### How the view mode affects extracted paths

The pathnames of extracted files follow **what you see in the file list**:

- In **Directory List** view with `Subdir` selected, extracting `MyFile` produces just
  `MyFile` in the output folder.
- In **Full List** view, the same file shows as `Subdir/MyFile`, so extracting it
  creates a `Subdir` folder containing `MyFile`.

Check **Strip Paths** to flatten everything regardless of view mode. 

---

## Importing and Exporting

Import/export behave like add/extract, but with a conversion applied.

**To import:** choose a **Conversion mode** in the panel, then **Actions → Import
Files…**, pick the files, and confirm.

**To export:** choose a **Conversion mode** (or **Best**), select the files in the file
list, choose **Actions → Export Files…**, navigate to the destination, and confirm.

As with extraction, existing files prompt for overwrite or skip. If the export
conversion is set to a *specific* converter rather than Best, only files suited to that
conversion are exported.
