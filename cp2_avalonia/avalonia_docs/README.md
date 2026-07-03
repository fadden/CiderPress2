# CiderPress II Desktop GUI Manual

Welcome to the manual for the **CiderPress II desktop application**, the
cross-platform graphical interface for managing Apple II and early Macintosh
disk images and file archives.

This documentation covers the **new Avalonia-based GUI** (`cp2_avalonia`), which
runs natively on **Windows, macOS, and Linux**. It supersedes the older
Windows-only WPF documentation.

> **New to CiderPress II?** Start with [Getting Started](getting-started.md) for a
> guided walkthrough.
>
> **Coming from the old WPF version?** See [What's New](whats-new.md) for a summary
> of everything that changed.
>
> **Looking for the command-line tool?** The text-based `cp2` utility is a separate
> program with its own, unchanged documentation. See the
> [Command-Line Manual](../docs/Manual-cp2.md).

---

## What CiderPress II Does

CiderPress II is a utility for managing **disk images** and **file archives**, with
a focus on the Apple II and early Macintosh. It supports disk images of floppies,
hard drives, CD-ROMs, and other media, along with popular archive formats such as
ShrinkIt. For easier interchange with modern systems, ZIP and gzip are also
supported.

You can browse the contents of an image, view files (including converting Apple II
graphics and documents to modern formats on the fly), add and extract files, edit
file attributes, repair and reorganize disks, and even edit raw sectors, all from
a single window.

---

## Contents

| Section | Description |
|---|---|
| [Getting Started](getting-started.md) | Install, launch, and open your first image |
| [Overview & Concepts](overview.md) | Disk images vs. archives; Add/Extract vs. Import/Export |
| [The Main Window](main-window.md) | Tour of the trees, file list, toolbar, and panels |
| [Viewing Files](viewing-files.md) | The file viewer, conversions, zoom, search |
| [Add / Extract / Import / Export](add-extract-import-export.md) | Getting files in and out, with attribute preservation |
| [Drag & Drop, Copy & Paste](drag-drop-clipboard.md) | Moving files within and between programs |
| [Editing Attributes](editing-attributes.md) | File types, dates, access flags, volume names |
| [Creating Archives](creating-archives.md) | New disk images and file archives |
| [Disk Operations](disk-operations.md) | Save As, replace partition, bad-block scan, defragment |
| [Sector & Block Editing](sector-editing.md) | The hex editor for raw disk data |
| [Settings](settings.md) | Application preferences, including themes |
| [Tools & Extras](tools.md) | ASCII chart, debug menu, log viewer |
| [What's New (for WPF users)](whats-new.md) | Changes from the old Windows version |
| [FAQ](faq.md) | Frequently asked questions |

---

*CiderPress II was created by Andy McFadden (faddenSoft). The cross-platform
Avalonia desktop application was created by Mark Long (Lydian Scale Software). Licensed under
the Apache License, Version 2.0.*
