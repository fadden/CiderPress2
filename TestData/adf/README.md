# ADF Samples #

These files are in AppleDouble File format.  The data fork is stored in a
regular file.  The resource fork, file type, and any extended attributes
are stored in a "header" file, which has the same name but starts with "._".
Less commonly, the may start with "%" or end with ".rsrc".

Note: some macOS file utilities will automatically merge ADF files into a 
single file with an actual resource fork.  If you copy or extract the test
tree with one of these programs, the cp2 CLI tests will fail because the
header files can't be found.  From a terminal window, copying files with
the "-X" flag will prevent this.
