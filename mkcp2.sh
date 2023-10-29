#!/bin/bash
#
# This builds the CiderPress II command-line utility ("cp2").  You must
# have the .NET development tools installed (this uses the "dotnet build"
# command).
#

case `uname -s | cut -c1-7` in
    Linux)
        runtime=linux-x64
        ;;
    Darwin)
        runtime=osx-x64
        ;;
    MINGW64)
        runtime=win-x64
        ;;
    *)
        echo "System not recognized"
        exit 1
esac

# generate framework-dependent debug build
# (add "--configuration release" for release build)
dotnet build cp2/cp2.csproj --runtime $runtime --no-self-contained
if [ $? -ne 0 ]; then
    echo "--- build failed"
    exit 1
fi

output_dir=cp2/bin/Debug/net6.0/$runtime

echo "--- build succeeded, files are in $output_dir:"
ls $output_dir
exit 0
