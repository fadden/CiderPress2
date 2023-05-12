# ADF Samples #

These files are in AppleDouble File format.  The data fork is stored in a
regular file.  The resource fork, file type, and any extended attributes
are stored in a "header" file, which has the same name but starts with "._".

Some Mac OS file utilities will automatically merge ADF files into a single
file with an actual resource fork.  This will cause the cp2 CLI tests to fail
because the header files can't be found.  From a terminal window, copying
files with the "-X" flag will prevent this.
