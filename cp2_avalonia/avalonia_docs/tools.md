# Tools & Extras

## ASCII Chart

**Tools → ASCII Chart…** opens a reference chart of character codes, handy when
sector-editing or interpreting raw data. It's a standalone window you can leave open
alongside your work.

---

## Help and About

- **Help → Help** (or **F1**, or the **?** toolbar button) opens this manual in your
  web browser.
- **Help → About CiderPress II** shows the version and credits.

---

## The DEBUG Menu

The DEBUG menu is hidden unless you turn on **Enable DEBUG menu** in
[Settings](settings.md). It collects diagnostic tools used during development; they are
not generally useful for everyday work, but are documented here for completeness.

- **DiskArc Library Tests…**: run the disk/archive library's self-tests.
- **FileConv Library Tests…**: run the file-conversion library's self-tests.
- **Bulk Compression Test…**: exercise the compression code across many files.
- **System Info…**: show runtime and environment details (useful in bug reports).
- **Show Debug Log**: toggle a live log window. The log viewer can **Copy to
  Clipboard** as well as **Save to File**, which is convenient when attaching logs to a
  bug report.
- **Show Drop/Paste Target**: overlay a visualization of drag-and-drop / paste target
  resolution, for diagnosing transfer issues.
- **Convert ANI to GIF…**: a utility for converting animated-cursor data to GIF.

If you're reporting a bug, the **System Info** dump and the **Debug Log** (copied to the
clipboard) are the most useful things to include.  
