#!/usr/bin/env python3
#
# Build the targets for various platforms, and create ZIP distribution
# packages.  This must be run in the top directory of the source tree.  All
# generated output files are placed in the "DIST" directory, which is
# reconstructed from scratch every time.
#
# The cp2_wpf target can only be built on Windows.  When this script is run on
# other platforms, the target will be skipped.
#
# Usage:
#  make-dist [-f] [--debug] [RIDs]
#    -f: remove DIST and obj/bin directories without asking
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
# NOTE: this will remove */bin/Release and */obj/Release as part of the build process.
#

import os
import os.path
import platform
import re
import shutil
import subprocess
import sys
import time
import zipfile

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
gForceRemove = False
gVersionTag = "UNKNOWN"

def GetVersionTag():
	""" GetVersionTag: extracts the version tag from GlobalAppVersion.cs and returns it. """

	pattern = re.compile(r'APP_VERS.*"([^"]*)"')
	with open("AppCommon/GlobalAppVersion.cs") as inFile:
		for lineNum, line in enumerate(inFile, start=1):
			found = re.search(pattern, line)
			if found:
				return found.group(1)

def ClobberBinObj():
	""" MakeClean: clobbers the 'Release' dir in 'bin' and 'obj' directories. """

	# "dotnet clean" only operates on a specific RID, and failed to remove the R2R outputs.
	# We want to just wipe the slate clean, so we remove */bin/Release and */obj/Release.
	configStr = "Release" if gReleaseConfig else "Debug"

	print("## Scrubbing obj/" + configStr + " and bin/" + configStr)
	scrubList = []
	with os.scandir('.') as itr:
		for entry in itr:
			if (not entry.is_dir):
				continue
			objPath = os.path.join(entry.name, "bin", configStr)
			if os.path.isdir(objPath):
				scrubList.append(objPath)
			binPath = os.path.join(entry.name, "obj", configStr)
			if os.path.isdir(binPath):
				scrubList.append(binPath)

	if scrubList:
		if (not gForceRemove):
			print("These directories will be removed:")
			for dirName in scrubList:
				print("  " + dirName)
			doRm = input("Continue (y/N)? ")
			if doRm != "y":
				print("Cancelled")
				sys.exit(0)

		for dirName in scrubList:
			shutil.rmtree(dirName)
	else:
		print("   (nothing to scrub)")

def BuildRid(rid, isSelfContained, distDir):
	""" BuildRid: executes the build commands for a given RID. """

	targets = list.copy(gBuildTargets)
	# Can only build cp2_wpf on Windows.
	if rid.startswith("win") and platform.system() == "Windows":
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
		print("--- building " + rid + " " + scArg + " " + target + debugStr)
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

	# Copy the pack-in files, like the README.  We could add these directly to the ZIP file,
	# but we may want to arrange them differently on different platforms.
	for fileName in gIncludeFiles:
		shutil.copy(fileName, outputDir)

	if rid.startswith("osx"):
		# Get a list of files in the output directory.
		fileList = os.listdir(outputDir)

		# Create the .app directory hierarchy.
		APP_FILE_NAME = "CiderPress II.app"
		appDir = os.path.join(outputDir, APP_FILE_NAME)
		contentsDir = os.path.join(appDir, "Contents")
		macOSDir = os.path.join(contentsDir, "MacOS")
		resourcesDir = os.path.join(contentsDir, "Resources")

		os.mkdir(appDir)
		os.mkdir(contentsDir)
		os.mkdir(macOSDir)
		os.mkdir(resourcesDir)

		# Move the build products into the Contents/MacOS sub-folder.
		for fileName in fileList:
			shutil.move(os.path.join(outputDir, fileName), macOSDir)

		# Copy Info.plist into Contents.
		shutil.copy("cp2_avalonia/Res/Info.plist", contentsDir)
		# Copy app icon into Contents/Resources.
		shutil.copy("cp2_avalonia/Res/cp2_app.icns", resourcesDir)

	# Create a ZIP archive to hold the contents.
	zipFileName = "cp2_" + gVersionTag + "_" + pkgName + ".zip"
	zipPathName = os.path.join(distDir, zipFileName)
	print("*** creating " + zipFileName)
	MakeZIP(zipPathName, outputDir)
	print()

def MakeZIP(zipPathName, sourcePath):
	""" MakeZIP: generate a ZIP file from the contents of a directory hierarchy. """

	COMP_LVL = 7
	OS_UNIX = 3
	S_IFREG = 0o100000      # (S_IFREG) - regular file
	EXEC_BITS = 0o755       # (S_IRWXU|S_IRGRP|S_IXGRP|S_IROTH|S_IXOTH) - permissions
	NOEXEC_BITS = 0o644 	# RW owner, R others

	with zipfile.ZipFile(zipPathName, 'x', compression=zipfile.ZIP_DEFLATED,
			compresslevel=COMP_LVL) as newZip:
		for root, dirs, files in os.walk(sourcePath):
			for fileName in files:
				pathName = os.path.join(root, fileName)
				zinfo = zipfile.ZipInfo.from_file(pathName, os.path.relpath(pathName, sourcePath))
				# We want the executables binaries to extract with the execute bits (0755) set on
				# macOS and Linux.  Other files should be 0644.  The default for files created
				# by zipfile is 0666, which is weird.
				bits = NOEXEC_BITS
				if fileName in gMarkExecutable:
					zinfo.create_system = OS_UNIX
					bits = EXEC_BITS
				zinfo.external_attr = (zinfo.external_attr & 0x0000ffff) | (bits << 16)
				with open(pathName, "rb") as fd:
					newZip.writestr(zinfo, fd.read(), zipfile.ZIP_DEFLATED, COMP_LVL)

def Main():
	""" Main """

	global gReleaseConfig
	global gForceRemove
	global gVersionTag

	gVersionTag = GetVersionTag()
	print("Building version " + gVersionTag)

	DIST_DIR = "DIST"

	args = sys.argv[1:]
	while args:
		if args[0] == "-f":
			gForceRemove = True
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
		if not gForceRemove:
			doRm = input("The " + DIST_DIR + " directory exists; remove it (y/N)? ")
			if doRm != "y":
				print("Cancelled")
				sys.exit(0)
		print("Removing " + DIST_DIR + "...")
		shutil.rmtree(DIST_DIR)

	distDir = os.path.abspath(DIST_DIR)
	os.mkdir(distDir)

	startTime = time.perf_counter()

	# You have to clean the outputs when switching between framework-dependent and
	# self-contained builds.  (This is especially a problem for cp2_avalonia with
	# ReadyToRun enabled, because the obj/../R2R files differ significantly.)

	ClobberBinObj()
	print("## Generating framework-dependent binaries...")
	for rid in ridList:
		print("### Publishing projects for RID=" + rid + "...")
		BuildRid(rid, False, distDir)

	ClobberBinObj()
	print("## Generating self-contained binaries...")
	for rid in ridList:
		print("### Publishing projects for RID=" + rid + "...")
		BuildRid(rid, True, distDir)

	endTime = time.perf_counter()
	print("Build completed, elapsed time", int(endTime - startTime), "seconds")

	sys.exit(0)

Main()	# does not return
