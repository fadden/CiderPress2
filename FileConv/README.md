# File Format Converters #

The files used on vintage systems may be in formats that modern systems don't recognize.  The
format converters "export" data from file archives and disk images to the host filesystem, and
"import" data from host files.

## Exports ##

All export converters are subclasses of `Converter`.  The constructor is passed a pair of streams,
for the data and resource forks, as well as the file attributes.  From this, the converter
decides if it can be applied to the data in question.  Some converters, like "plain text" and
"hex dump", can be applied to everything.  Others, like screen image captures, are more limited.
Sometimes more than one converter might be appropriate, but some are more likely to work than
others, so each converter instance is assigned an applicability rating.

The `ExportFoundry` class has a list of all known converters.  It can apply each of them to an
input file and produce a list of applicable instances, sorted by rating.

When the converter is executed, it generates an instance of `IConvOutput`.  These are generally
format-agnostic, easy to convert into something else.  The current classes are:

 - `SimpleText`: a Unicode text string.  The end-of-line markers used match those of the
   host system (LF for Mac/UNIX, CRLF for Windows).  It can be output as a text file with
   UTF-8 encoding (.txt) using `TXTGenerator`.
 - `FancyText`: a subclass of SimpleText that adds format annotations.  For example, if the
   foreground text color changes to red on the 47th character, FancyText adds {47, color=red}.
   This can be converted into Rich Text Format (.rtf) with `RTFGenerator`.  If you don't want the
   extra formatting, it can simply be treated as a SimpleText.
 - `IBitmap`: bitmap graphics.  This can be a `Bitmap8` for images with an 8-bit color palette,
   or `Bitmap32` for ARGB truecolor.  These are converted to Portable Network Graphics (.png)
   using `PNGGenerator`.
 - `CellGrid`: grid of cells, such as a spreadsheet.  This can be converted to a Comma-Separated
   Value (.csv) file with `CSVGenerator`.
 - `HostConv`: not really an output format, this is used when a format like GIF or JPEG is found
   in a file archive or disk image.  It acts as a signal to the viewer that the file should be
   extracted and viewed with host facilities.  (The FileConv library is not able to decode GIF,
   JPEG, or PNG images to IBitmap.)

Exporters are not expected to throw exceptions when bad data is encountered.  If the exporter
has declared itself to be applicable, it is expected to do the best it can with what it has.
The converter can generate notes that may be shown to the user.

## Imports ##

All import converters are subclasses of `Importer`.  Importers do not try to auto-detect formats.
The specific class must be instantiated and the data provided.  An applicability test is still
provided for the benefit of applications that wish to probe for a valid format.

Importers provide a function that optionally strips file extensions off, so that a BASIC program
exported to "PROGRAM.txt" will be written back to the disk as "PROGRAM".  The importer also
provides ProDOS and HFS file type values.

The conversion is done by reading one stream and writing directly to either or both data and
resource forks.  There is no equivalent to `IConvOutput` for importers.  Conversion failures are
reported by throwing an exception.

## General Notes ##

File converters are expected to work equally well with files from disk images, file archives,
and the host filesystem.  The library has dependencies on DiskArc, but that's limited to
special features like sparse-file handling, or auto-selection of high ASCII when importing
text files to a DOS disk.

The various subdirectories (Generic/Code/Doc/Gfx) exist to make source code easier to find.  They
have no meaning beyond that.

When exporting files from DOS disks that were read in "raw" mode, a flag is set internally that
lets the export converter know that certain files will look different.  Generally speaking,
converters won't try to handle raw-mode files; the option is primarily there for the benefit of
the random-access text converter.

### Configuration ###

Each converter can be passed a list of options that change the way the conversion is performed.
For example, graphics converters could have color vs. black & white, text converters can choose
which character set to use, and disassemblers might want to perform syntax highlighting.  Each
converter potentially has a unique set of options.

There are a few ways to handle this in the GUI interface:

 1. Let each converter define the set of controls it needs (checkboxes, radio controls, etc).
    The controls are generated on the fly.  This is doable with WPF (see 6502bench SourceGen's
    visualization editor, which works with "plugins"), but not all UI toolkits may support this.
 2. Define all possible options for all known converters, but only show the relevant ones.
    For example, there would be a "black & white" checkbox that could be used by any converter
    that wants to offer the choice of black & white vs. color.  This is straightforward, but
    requires defining a large set of controls, and the viewer would need to be updated whenever
    a new unique option is needed.
 3. Define a collection of "blank" options, e.g. 3 checkboxes and two sets of four radio buttons,
    and generate a customized mapping for each converter.  The viewer would only need to be
    updated if a converter needed more than what was defined, or we wanted to handle a different
    type of control (maybe a slider).  This avoids the need to generate controls dynamically,
    but requires some logic to manage the mapping.

The CLI interface needs to be able to set options with short name/value strings.  For example,
setting the DHGR viewer to black & white might be done with "dhgr,bw=true".  User defaults can
be stored in the settings file in human-readable form.

Because the CLI needs to work with strings, the GUI should pass options around internally as
name/value pairs.  This is simpler than binding controls to properties in the converter instances.

The controls we need are:
 - Checkboxes, for boolean values (e.g. syntax highlighting on BAS).  65x02 disassembler
   will need at least 3 (undocumented, single-byte BRK, wide/narrow '816).
 - Integer input fields, for a few things (like random access text file record lengths).  It's
   important to allow "no value", so that converters like the random-access text exporter has
   the option of setting the default from the aux type.
 - Multiple-choice, for selection of character sets or certain graphics conversions.  These
   can be presented as radio buttons or combo boxes.  Radio buttons are nicer for the user
   because they show all possible values on-screen.

## Tests ##

Tests are in the "Tests" directory.  These can be run with `cp2 debug-test-fc` or from the
GUI application's hidden DEBUG menu.
