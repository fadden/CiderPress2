#!/usr/bin/python3
#
# Sets the contents of the "prevnext" sections, which have the prev/next
# buttons.  Pass the directory where the HTML files live as the first
# command-line argument.  The ordered list of HTML documents will be
# read out of "topic-list.txt" in that directory.
#

import filecmp
import os.path
import re
import sys

class LocalError(Exception):
    """Errors generated internally"""
    pass

# Regex pattern for prevnext section.
findChunk = re.compile(
    r'^\s*<div id="prevnext">\s*$.'
    r'(.*?)'
    r'^\s*<\/div>',
    re.DOTALL | re.MULTILINE)
GROUP_ALL = 0
GROUP_CHUNK = 1


def editFile(fileList, index, outFileName):
    """ Edit a file, replacing blocks with the contents of other files. """

    inFileName = fileList[index]
    try:
        with open(inFileName, "r") as inFile:
            fileData = inFile.read()
        outFile = open(outFileName, "x")
    except IOError as err:
        raise LocalError(err)

    # Find first (and presumably only) matching chunk.
    match = findChunk.search(fileData)
    if not match:
        print("== No prevnext section found")
        return

    replSpan = match.span(GROUP_CHUNK)

    chunk = fileData[replSpan[0] : replSpan[1]]
    print("== Matched {0}:{1}".format(replSpan[0], replSpan[1]))


    # copy everything up to the chunk
    outFile.write(fileData[ : replSpan[0]])
    # insert the file, tweaking active ID if appropriate
    generatePrevNext(fileList, index, outFile)
    # copy the rest of the file, including the </div>
    outFile.write(fileData[match.end(GROUP_CHUNK) : ])

    print("== done")
    outFile.close()

    return


def generatePrevNext(fileList, index, outFile):
    """ Generate prev/next button HTML """

    # <a href="#" class="btn-previous">&laquo; Previous</a>
    # <a href="#" class="btn-next">Next &raquo;</a>
    if index > 0:
        outFile.write('    <a href="')
        outFile.write(fileList[index - 1])
        outFile.write('" class="btn-previous">&laquo; Previous</a>\n')

    if index + 1 < len(fileList):
        outFile.write('    <a href="')
        outFile.write(fileList[index + 1])
        outFile.write('" class="btn-next">Next &raquo;</a>\n')

    return


def main():
    """ main """

    if len(sys.argv) != 2:
        print("Usage: prevnext <subdir>");
        sys.exit(1)

    outFileName = None

    subdir = sys.argv[1]
    os.chdir(subdir)

    with open("topic-list.txt") as afile:
        fileList = [line.rstrip() for line in afile]

    try:
        for index in range(len(fileList)):
            name = fileList[index]
            print("Processing #{0}: {1}".format(index, name))
            outFileName = name + "_NEW"
            editFile(fileList, index, outFileName)

            # See if the file has changed.  If it hasn't, keep the original
            # so the file dates don't change.
            if filecmp.cmp(name, outFileName, False):
                print("== No changes, removing new")
                os.remove(outFileName)
            else:
                print("== Changed, keeping new")
                os.replace(outFileName, name)
    except LocalError as err:
        print("ERROR: {0}".format(err))
        if outFileName:
            print("  check " + outFileName)
        sys.exit(1)

    sys.exit(0)


main()  # does not return
