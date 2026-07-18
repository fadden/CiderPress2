#!/usr/bin/env python
#
# Make distribution packages.  This must be run in the top directory of the
# source tree.  All generated output files are placed in the "DIST" directory,
# which is reconstructed from scratch.
#
# Usage:
#  make-dist [-f] [--debug] [RIDs]
#    -f: if DIST directory exists, remove it without asking
#    --debug: generate Debug builds instead of Release
#    RIDs: one or more RID strings
#
# NOTE: on Windows+MINGW use "winpty python Pkg/make-dist.py" to avoid issues
# with stdin freezing up.  (In theory this was fixed in python 3.14, but not
# in practice.)
#
# By default, this will build all supported RIDs, in Release mode, and
# generate appropriate distribution packages (.zip).
#

import os
import os.path
import shutil
import subprocess
import sys
import time
import zipfile

VERSION_TAG = "2.0.0-dev3"		# TODO: generate this automatically

# Default set of Runtime Identifiers.
gDefaultRids = [
	"win-x86",			# 32-bit x86 Windows
	"win-x64",          # 64-bit x64 Windows
	"linux-x64",        # 64-bit x64 Linux
	"osx-x64",          # 64-bit x64 macOS
	"osx-arm64",        # 64-bit ARM macOS
]
# Targets to build for all platforms.
gBuildTargets = [
	"cp2",				# CLI tool
	"cp2_avalonia",		# multi-platform GUI tool
]
# Additional targets to build for Windows platforms.
gWinBuildTargets = [
	"cp2_wpf",			# Windows-only GUI tool
]
gMarkExecutable = {
	"cp2",
	"CiderPress2",
}
# Files to add to the distribution.
gIncludeFiles = [
	"Pkg/README.txt",
    "LegalStuff.txt",
    "docs/Manual-cp2.md",
    "Pkg/sample.cp2rc",
]

gReleaseConfig = True


def BuildRid(rid, isSelfContained, distDir):
	""" BuildRid: executes the build commands for a given RID. """

	targets = list.copy(gBuildTargets)
	if rid.startswith("win"):
		targets += gWinBuildTargets

	# Create output directory.
	scStr = "_sc" if isSelfContained else "_fd"
	debugStr = "" if gReleaseConfig else "_debug"
	pkgName = rid + scStr + debugStr
	outputDir = os.path.join(distDir, pkgName)
	os.mkdir(outputDir)

	scArg = "--sc" if isSelfContained else "--no-self-contained"
	configArg = "Release" if gReleaseConfig else "Debug"

	# Publish all targets.  A post-publish step in the .csproj file copies all of the outputs
	# to _AllAppsDir.  We're building single-file outputs, so none of the files would clash.
	# (If we left things as DLLs, parameters like ReadyToRun would affect the output.)
	for target in targets:
		print("--- publishing " + rid + " " + scArg + " " + target + debugStr)
		args = [ "dotnet", "publish", target, "-r", rid, scArg, "-c", configArg,
			"-p:_AllAppsDir=" + outputDir ]
		if gReleaseConfig:
			args.append("-p:DebugSymbols=false")
		result = subprocess.run(args, capture_output=True, encoding="utf-8")
		if result.returncode != 0:
			print("FAILED: ", args)
			print("STDOUT: ", result.stdout)
			print("STDERR: ", result.stderr)
			sys.exit(1)

		print(result.stdout)		# could be shown only with a "verbose" flag?

	# Create a ZIP archive to hold the contents.
	COMP_LVL=7
	buildProducts = os.listdir(outputDir)
	zipFileName = "cp2_" + VERSION_TAG + "_" + pkgName + ".zip"
	zipPathName = os.path.join(distDir, zipFileName)
	print("*** creating " + zipFileName)
	with zipfile.ZipFile(zipPathName, 'x', compression=zipfile.ZIP_DEFLATED,
			compresslevel=COMP_LVL) as newZip:
		OS_UNIX = 3
		S_IFREG = 0o100000      # (S_IFREG) - regular file
		EXEC_BITS = 0o755       # (S_IRWXU|S_IRGRP|S_IXGRP|S_IROTH|S_IXOTH) - permissions
		NOEXEC_BITS = 0o644 	# RW owner, R others
		for fileName in gIncludeFiles:
			# Add the pack-in files, such as the README.
			#
			# In theory, we can just use newZip.write() and skip all the nonsense above.
			# In practice, this causes the files to be generated with perms 0666, even when
			# running on Windows, which is dumb and annoying.  I can't see a way to make it
			# behave correctly, so we do it the hard way.
			#newZip.write(fileName, os.path.basename(fileName))
			inputPath = fileName
			zinfo = zipfile.ZipInfo.from_file(inputPath, os.path.basename(inputPath))
			zinfo.external_attr = (zinfo.external_attr & 0x0000ffff) | (NOEXEC_BITS << 16)
			with open(inputPath, "rb") as fd:
				newZip.writestr(zinfo, fd.read(), zipfile.ZIP_DEFLATED, COMP_LVL)
		for fileName in buildProducts:
			# Grab every file in the multi-target output directory.
			inputPath = os.path.join(outputDir, fileName)
			zinfo = zipfile.ZipInfo.from_file(inputPath, fileName)
			# Set the OS to "UNIX" and specify file permissions.  This is most important for the
			# executables, so that they unzip with the execute bit set on macOS/Linux.
			zinfo.create_system = OS_UNIX
			bits = EXEC_BITS if fileName in gMarkExecutable else NOEXEC_BITS
			zinfo.external_attr = (zinfo.external_attr & 0x0000ffff) | (bits << 16)
			with open(inputPath, "rb") as fd:
				newZip.writestr(zinfo, fd.read(), zipfile.ZIP_DEFLATED, COMP_LVL)
	print()

def Main():
	""" Main """

	global gReleaseConfig
	DIST_DIR = "DIST"
	forceRemove = False

	args = sys.argv[1:]
	while args:
		if args[0] == "-f":
			forceRemove = True
		elif args[0] == "--debug":
			gReleaseConfig = False
		else:
			break
		args = args[1:]

	ridList = args
	if not ridList:
		ridList = gDefaultRids

	if not os.path.exists("CiderPress2.sln"):
		print("This can only be run from the root of the source tree.")
		sys.exit(1)

	if os.path.exists(DIST_DIR):
		if not forceRemove:
			doRm = input("The " + DIST_DIR + " directory exists; remove it (y/N)? ")
			if doRm != "y":
				print("Cancelled")
				sys.exit(0)
		print("Removing " + DIST_DIR + "...")
		shutil.rmtree(DIST_DIR)

	distDir = os.path.abspath(DIST_DIR)
	os.mkdir(distDir)

	print("### Building for version " + VERSION_TAG)

	startTime = time.perf_counter()

	for rid in ridList:
		print("### Generating " + rid + "...")
		BuildRid(rid, False, distDir)
		BuildRid(rid, True, distDir)

	endTime = time.perf_counter()
	print("Build completed, elapsed time", int(endTime - startTime), "seconds")

	sys.exit(0)

Main()	# does not return
