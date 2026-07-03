# Overview & Concepts

This page explains the core ideas that the rest of the manual builds on. If you've
used the original CiderPress or the command-line `cp2` tool, much of this will be
familiar.

---

## Disk Images and File Archives

CiderPress II works with two fundamentally different kinds of container.

A **disk image** is a file containing the contents of a floppy disk, hard drive,
CD-ROM, or other physical medium. Disk images were typically created on vintage
systems so the contents could be transferred electronically. They often (but not
always) contain a filesystem such as DOS 3.3, ProDOS, or HFS. Examples:
`.do`, `.po`, `.dsk`, `.2mg`, `.woz`, `.hdv`, DiskCopy `.dc`/`.image`.

A **file archive** is a single file that bundles a collection of other files,
usually compressed to save space. Examples: ShrinkIt (`.shk`/`.sdk`), Binary II
(`.bny`/`.bqy`), AppleSingle (`.as`), ZIP (`.zip`), and gzip (`.gz`).

Throughout this manual, "archive" is sometimes used loosely to mean *either* a disk
image or a file archive.

### Why the distinction matters

A filesystem on a disk image can be modified in place: adding or deleting a file
disturbs little else. A file archive, by contrast, generally has to be rebuilt:
CiderPress II writes an entirely new archive file and renames it over the original
once all changes are complete.

This affects what happens if an operation is interrupted:

- **File archive**: canceling partway through leaves the original archive
  *untouched*. The worst case is a stray temporary file.
- **Disk image**: changes are written directly, so canceling after adding two of
  three files leaves those two files present. Killing the application mid-write to a
  disk image could, in principle, corrupt it, just like pulling the power on a real
  machine. CiderPress II flushes all changes before returning to idle, so data is
  safe once an operation finishes, and steps are taken to keep things consistent
  whenever it pauses to ask you a question.

> Because rebuilding a file archive needs room for both the old and new copies,
> archives stored *inside* a small disk image can run out of space. This is rarely
> an issue for archives on your host drive.

### Nested archives ("turduckens")

Disk images and file archives can be stored inside one another. A ShrinkIt archive
might live on a ProDOS filesystem inside a WOZ image inside a ZIP file. CiderPress II
fully supports this nesting and lets you reach the files at any level directly, with no
need to extract layer by layer. How deeply it scans automatically is controlled by
the **Auto-Open Depth** setting (see [Settings](settings.md)).

---

## Add/Extract vs. Import/Export

CiderPress II keeps four distinct operations for moving files in and out. The key
difference is whether the file's *contents* are converted.

| Operation | Direction | Conversion |
|---|---|---|
| **Add** | Host → archive | None. Restores attributes from saved metadata if present. |
| **Extract** | Archive → host | None. Preserves attributes (forks, type, dates). |
| **Import** | Host → archive | Yes. E.g. convert a text file's line endings, or tokenize Applesoft BASIC. |
| **Export** | Archive → host | Yes. E.g. convert an Apple II hi-res image to PNG, or a document to plain text. |

Older tools such as NuLib2 and the original CiderPress blurred these together, which
could lead to ambiguous behavior. In CiderPress II, **add/extract are always raw,
faithful transfers**, while **import/export always involve a format conversion**.

The right-hand settings panel and the toolbar **Drag & Copy mode** selector let you
choose which pair is active for a given operation. This is covered in detail in
[Add / Extract / Import / Export](add-extract-import-export.md).

---

## How CiderPress II Differs from the Original CiderPress

The original CiderPress (2003) was a Windows-only C++/MFC application. CiderPress II
is written in C# / .NET, gives equal weight to its GUI and command-line interfaces,
and runs on Windows, macOS, and Linux.

Notable capabilities beyond the original:

- File archives and disk images **nested inside** other containers can be accessed
  directly.
- When extracting, resource forks and extended attributes can be preserved as
  **AppleSingle**, **AppleDouble**, **NAPS** (NuLib2 Attribute Preservation
  Strings), or via host filesystem features (HFS+ on macOS). These are recombined
  transparently when adding.
- DOS T/I/A/B files can be opened in **"raw"** mode.
- Files can be **copied directly between volumes**; for DOS files this can preserve
  the sparse structure of random-access text files.
- AppleSingle/AppleDouble are integrated into add/extract (not treated as read-only).
- **DOS hybrid** disks (e.g. DOS + ProDOS on one disk) are supported, and
  DOS.MASTER embedded volumes are handled much better.
- HFS type support is generalized; ProDOS and HFS types can be set independently.
- Low-level warnings from filesystem code are surfaced to you as **"notes"**.

A few things were dropped for lack of interest: NuFX entries compressed with deflate
or bzip2, the FDI disk image format, and SST file combining.

---

## How CiderPress II Differs from the WPF Version

If you've used the previous Windows-only desktop build (the WPF version), the
Avalonia application keeps the same overall workflow but adds cross-platform support,
a dark theme, an improved file viewer, an inline file finder, and a number of
quality-of-life refinements. See [What's New](whats-new.md) for the full list.

