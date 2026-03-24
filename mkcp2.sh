#!/usr/bin/env bash
#
# This builds the CiderPress II command-line utility ("cp2").  You must have
# the .NET SDK installed (this uses the "dotnet build" command).
#
# Usage:
#  mkcp2.sh [--runtime RUNTIME] [--debug] [--self-contained]
#
# By default, this will do a release build for the current operating system
# and CPU architecture.  You can override the runtime by specifying the
# target RID, which should be something like "win-x64" or "linux-arm64"
# (see https://learn.microsoft.com/en-us/dotnet/core/rid-catalog for details).
#
# For self-contained builds, the runtime must be specified explicitly.
#

# There doesn't appear to be a way to query the system's RID.  If you don't
# specify one, it creates a "portable, framework-dependent app", which is fine.

config=Release
runtime=
selfcont=
while [ x$1 != x ]; do
    case $1 in
        --debug)
            config=Debug
            shift
            continue;;
        --self-contained)
            selfcont=--self-contained
            shift
            continue;;
        --runtime)
            runtime=$2
            shift
            shift
            continue;;
        *)
            echo "Unknown argument: $1"
            exit 1;;
    esac
done

# This is a thing.
if [ ! -z "$selfcont" -a -z "$runtime" ]; then
    echo "Error: must explicitly specify runtime for self-contained builds"
    exit 1
fi

# One component of the output directory is determined by the TargetFramework
# setting in the cp2.csproj XML file.  There doesn't appear to be a built-in
# way to query this, so we do it the awkward way.
tver=$(awk -F "[><]" '/TargetFramework/{ print $3 }' cp2/cp2.csproj)
output_dir=cp2/bin/$config/$tver

if [ -z "$runtime" ]; then
    echo "--- building for current system ($config)"
    rtarg=
else
    echo "--- building for $runtime ($config)"
    rtarg="--runtime $runtime"
    output_dir=$output_dir/$runtime
fi

dotnet build cp2/cp2.csproj --configuration=$config $rtarg $selfcont
if [ $? -ne 0 ]; then
    echo "--- build failed"
    exit 1
fi

echo "--- build succeeded, files are in $output_dir:"
ls $output_dir
exit 0
