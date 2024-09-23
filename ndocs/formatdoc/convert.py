#!/usr/bin/python3
#
# Usage: convert <md-path|all>
#  where md-path is e.g. "DiskArc/Arc/AppleLink-notes.md"
#
# This formats various Markdown documents as HTML.  This is done via the
# github REST API, using code from
# https://github.com/phseiff/github-flavored-markdown-to-html .  You need to
# have that program installed (pip3 install gh-md-to-html) for this to work.
#
# The file needs to be fixed up a bit before and after the conversion.
#
# NOTE: github has a limit of 60 requests per hour for an unauthenticated user.
# If you exceed the limit, the files will be trivial "limit exceeded" messages
# instead of converted documents.  The limit is increased to 5,000 for
# authenticated requests.  To add authentication:
#  - Create a fine-grained personal access token.  Learn how on
#    https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens
#  - Locate the gh-md-to-html module.  In Windows, I found this in
#    ~/AppData/Local/Packages/PythonSoftwareFoundation.Python.3.12_qbz5n2kfra8p0/LocalCache/local-packages/Python312/site-packages/gh_md_to_html
#  - Edit the __init__.py file.  Find the "markdown_to_html_via_github_api"
#    function.  Add two entries to "headers", substituting the token in:
#      "Authorization": "Bearer github_pat_####",
#      "X-GitHub-Api-Version": "2022-11-28"
#
# The converter has an "offline" conversion mode, which requires additional
# dependencies (pip3 install gh-md-to-html[offline_conversion]), but it
# currently fails in pygments.
#

import os
import os.path
import re
import shutil
import subprocess
import sys

import gh_md_to_html

gFileList = [
        "DiskArc/Arc/AppleLink-notes.md",
        "DiskArc/Arc/AppleSingle-notes.md",
        "DiskArc/Arc/Binary2-notes.md",
        "DiskArc/Arc/GZip-notes.md",
        "DiskArc/Arc/MacBinary-notes.md",
        "DiskArc/Arc/NuFX-notes.md",
        "DiskArc/Arc/StuffIt-notes.md",
        "DiskArc/Arc/Zip-notes.md",
        "DiskArc/Comp/LZC-notes.md",
        "DiskArc/Comp/NuLZW-notes.md",
        "DiskArc/Comp/Squeeze-notes.md",
        "DiskArc/Disk/DiskCopy-notes.md",
        "DiskArc/Disk/Nibble-notes.md",
        "DiskArc/Disk/Trackstar-notes.md",
        "DiskArc/Disk/TwoIMG-notes.md",
        "DiskArc/Disk/Unadorned-notes.md",
        "DiskArc/Disk/Woz-notes.md",
        "DiskArc/FS/CPM-notes.md",
        "DiskArc/FS/DOS-notes.md",
        "DiskArc/FS/Gutenberg-notes.md",
        "DiskArc/FS/HFS-notes.md",
        "DiskArc/FS/Hybrid-notes.md",
        "DiskArc/FS/MFS-notes.md",
        "DiskArc/FS/Pascal-notes.md",
        "DiskArc/FS/ProDOS-notes.md",
        "DiskArc/FS/RDOS-notes.md",
        "DiskArc/Multi/APM-notes.md",
        "DiskArc/Multi/CFFA-notes.md",
        "DiskArc/Multi/DOS800-notes.md",
        "DiskArc/Multi/FocusDrive-notes.md",
        "DiskArc/Multi/MacTS-notes.md",
        "DiskArc/Multi/MicroDrive-notes.md",
        "DiskArc/Multi/PPM-notes.md",
        "FileConv/Code/ApplePascal-notes.md",
        "FileConv/Code/BASIC-notes.md",
        "FileConv/Code/Disasm65-notes.md",
        "FileConv/Code/LisaAsm-notes.md",
        "FileConv/Code/MerlinAsm-notes.md",
        "FileConv/Code/OMF-notes.md",
        "FileConv/Code/SCAsm-notes.md",
        "FileConv/Doc/AppleWorks-notes.md",
        "FileConv/Doc/AWGS-notes.md",
        "FileConv/Doc/GutenbergWP-notes.md",
        "FileConv/Doc/MagicWindow-notes.md",
        "FileConv/Doc/Teach-notes.md",
        "FileConv/Generic/ResourceFork-notes.md",
        "FileConv/Gfx/BitmapFont-notes.md",
        "FileConv/Gfx/DoubleHiRes-notes.md",
        "FileConv/Gfx/Fontrix-notes.md",
        "FileConv/Gfx/GSFinderIcon-notes.md",
        "FileConv/Gfx/HiRes-notes.md",
        "FileConv/Gfx/MacPaint-notes.md",
        "FileConv/Gfx/PrintShop-notes.md",
        "FileConv/Gfx/ShapeTable-notes.md",
        "FileConv/Gfx/SuperHiRes-notes.md",
        ]

text_subst = [
        ( re.compile(r"/github-markdown-css/"), "" ),
        ]

def convert(pathName):
    """ converts a .md file from the source tree to a .html file here """

    tempmd = "tempfile.md"
    temphtml = "tempfile.html"

    # In theory we can call gh_md_to_html.core_converter.markdown(), but in
    # practice python can't find that.  The file loader is confused by
    # byte-order marks, so we need to read the file and write it back without
    # the BOM.
    fileNameBase = os.path.splitext(os.path.basename(pathName))[0]
    treePath = "../../" + pathName
    with open(treePath, "r", encoding="utf-8-sig") as file:
        mdtext = file.read()
    with open(tempmd, "w", encoding="utf-8") as file:
        file.write(mdtext)

    footer = ("<p><a href=\"../doc-index.html\">Return to documentation index</a> | "
        "<a href=\"https://github.com/fadden/CiderPress2/blob/main/" + pathName + "\">"
        "View in source tree</a></p>\n")

    # Do the conversion.
    gh_md_to_html.main(tempmd, math="False", footer=footer, box_width="25cm")
            #core_converter="OFFLINE"

    # Modify the HTML.  I can't find a way to make the CSS reference
    # relative, so we just modify that here.
    with open(temphtml, "r", encoding="utf-8") as infile:
        filedata = infile.read()
    for subst in text_subst:
        filedata = re.sub(subst[0], subst[1], filedata)
    with open(fileNameBase + ".html", "w", encoding="utf-8") as outfile:
        outfile.write(filedata)

    os.remove(tempmd)
    os.remove(temphtml)


def main():
    """ main """

    fileList = sys.argv[1:]
    if not fileList:
        print("Usage: convert <md-path|all>")
        sys.exit(2)

    if fileList[0] == "all":
        fileList = gFileList

    for pathName in fileList:
        print("convert " + pathName)
        convert(pathName)

    # Get rid of the extra stuff.
    if os.path.exists("github-markdown-css"):
        shutil.rmtree("github-markdown-css")
    if os.path.exists("images"):
        os.removedirs("images")
    sys.exit(0)

main()  # does not return
