#!/usr/bin/python3
#
# This publishes a new version of the web site from the "ndocs" directory
# to the "docs" directory.  It should be run from "ndocs".
#
# The "app_version" should be updated with the release information.
#

import os
import os.path
import re
import shutil
import subprocess
import sys


# Current version string.  Used for text substitution.
app_version = "0.4.0-dev1"
pkg_version = "0.4.0-d1"




# Output directory.  The directory will be completely removed and regenerated.
output_dir = "../pdocs"     # DEBUG - change to "../docs"

# Subdirectory with files that will be copied to the top of the project.
top_dir = "top"

# List of directories to exclude from recursive copy.
copy_exclude_dirs = [ top_dir ]

# List of file patterns to exclude from recursive copy.
copy_exclude_files = [
        re.compile(r"^.*\.py$"),        # python scripts
        re.compile(r"^incl-.*$"),       # HTML include files
        re.compile(r"^\..*\.swp$"),     # VIM temporary files
        re.compile(r"topic-list.txt")   # prev/next order file
        ]

# Text variable substitutions.  Performed on ".html" and ".md".
text_subst = [
        ( re.compile(r"\${VERSION}"), app_version ),
        ( re.compile(r"\${PKG_VERSION}"), pkg_version )
        ]

def copy_file(inpath, outpath, do_subst):
    """
    Copy the contents of a file, optionally performing a variable
    substitution.
      inpath: path to input file
      outpath: path to output file
      do_subst: if True, perform variable substitution
    """
    if do_subst:
        with open(inpath, "r", encoding="utf-8") as infile:
            filedata = infile.read()

        if do_subst:
            for subst in text_subst:
                filedata = re.sub(subst[0], subst[1], filedata)

        with open(outpath, "x", encoding="utf-8") as outfile:
            outfile.write(filedata)
    else:
        shutil.copyfile(inpath, outpath)

def main():
    """ main """

    # See if we're in the right place.
    if not os.path.isfile("publish.py"):
        print("Failed: run this script from the ndocs directory.")
        sys.exit(1)

    # Quick check to see if the source and destination directories are at the
    # same level in the hierarchy.  This is really just a way to check that
    # our removal of the target directory isn't stepping outside the project
    # area.  It can be removed if you want to install elsewhere.
    curdir = os.path.normpath(os.getcwd())
    outdir = os.path.normpath(os.path.join(os.getcwd(), output_dir))
    if (os.path.dirname(curdir) != os.path.dirname(outdir)):
        raise Exception("source and target directory are at different levels")

    # Remove and create the output directory.
    print("--- Removing output directory", outdir)
    shutil.rmtree(outdir, True)
    print("--- Creating output directory", outdir)
    os.mkdir(outdir)

    # Do prev/next patching for tutorials.
    print("--- Configuring tutorial prev/next links")
    for tutdir in ["cli-tutorial", "gui-tutorial"]:
        result = subprocess.run(["py", "prevnext.py", tutdir],
                capture_output=True, encoding="utf-8")
        if result.returncode != 0:
            print("FAILED prevnext in", tutdir)
            print("STDOUT: ", result.stdout)
            print("STDERR: ", result.stderr)
            sys.exit(1)

    # Walk through the directory hierarchy.
    print("--- Copying files", outdir)
    for root, dirs, files in os.walk(os.path.curdir):
        # Sort the lists to make the output more consistent.
        dirs.sort()
        files.sort()

        # Is this directory excluded from copying?
        rootbasename = os.path.basename(root)
        if rootbasename in copy_exclude_dirs:
            print(" - copy not traversing:", rootbasename)
            continue

        # According to the python docs, if # "topdown=True" then we can
        # modify the list during the traversal.  Use this to prune the
        # directory list.  https://stackoverflow.com/a/19859907/294248
        dirs[:] = [d for d in dirs if d not in copy_exclude_dirs]

        # Create target directory.
        for dir in dirs:
            fulldir = os.path.join(root, dir)
            newdir = os.path.relpath(os.path.join(outdir, fulldir))
            print(" + creating directory:", newdir)
            os.mkdir(newdir)

        for file in files:
            fullname = os.path.normpath(os.path.join(root, file))
            # See if it's in the exclusion list.
            exclude = False
            for regexp in copy_exclude_files:
                if regexp.match(file):
                    print(" - not copying:", fullname)
                    exclude = True
                    break;
            if exclude:
                continue

            # Check the filename extension.
            rootext = os.path.splitext(file)
            do_subst = False
            if rootext[1] == ".html":
                # Do special processing for HTML.
                print(" * performing block replace:", fullname)
                result = subprocess.run(["py", "block-repl.py", fullname],
                        capture_output=True, encoding="utf-8")
                if result.returncode != 0:
                    print("FAILED:", result.stderr)
                    result.check_returncode()
                do_subst = True
            elif rootext[1] == ".md":
                do_subst = True

            # Copy the file.
            outname = os.path.relpath(os.path.join(outdir, fullname))
            print(" + copying:", fullname, "to", outname)
            copy_file(fullname, outname, do_subst)

    # Copy files to the top level.
    outdir = ".."
    print("--- Copying top files to", os.path.normpath(os.path.join(os.getcwd(), outdir)))
    topfiles = os.listdir(top_dir)
    for file in topfiles:
        inpath = os.path.join(top_dir, file)
        outpath = os.path.join(outdir, file)
        #if os.path.isfile(outpath):
        #    os.remove(outpath)
        #copy_file(inpath, outpath, True)
        print(" WOULD copy", inpath, "-->", outpath)

    sys.exit(0)

main()  # does not return
