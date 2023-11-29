# ciderpress2.com Web Site #

This is the content for the CiderPress II web site (https://ciderpress2.com/),
which provides documentation for the project.  Most of the files exist in two
places, `docs` and `ndocs`.

The `docs` directory is where github serves web pages from.  The contents
should always match up with the current release.

The `ndocs` directory is where changes are made during development.  The
files here document the tip-of-tree behavior.  When a software update is
released, the documents are published to the `docs` directory.  This directory
also has "include" files with navigation bar components and scripts that help
manage updates.

Many of the pages share common elements: masthead, top nav bar, side nav bar,
and footer.  These are inserted by a Python script (block-repl.py) that must
be run whenever one of the "-incl" files changes.

Some text variable substitutions can be performed on HTML and Markdown files,
e.g. the app version is ${VERSION}.
