# TurDuckEn Samples #

These hold nested disk and file archives, useful for exercising utility
programs.

    MultiPart.hdv
      Partition #1
        ProDOS /Mixed.Disk
          Embed #1
            DOS v001
          Embed #2
            DOS v002
          Embed #3
            DOS v003
          Embed #4
            DOS v004
          Embed #5
            DOS v005
      Partition #2
        HFS :New Disk
          Dir1:Dir2:new-init.do (disk image)
          Dir1:Dir2:Samples.BXY (NuFX file archive)
          SIMPLE.DOS.SDK (NuFX disk archive)
          small.woz.gz (gzip-compressed WOZ disk image)
          Some.Files.zip

    Shrunk.zip.gz
      Shrunk.zip
        Binary2.SHK
          SAMPLE.BQY

    subdirs.zip
      zdir1:zdir2:wrapdirhello.shk
        ndir1:ndir2:dirhello.shk

    WrappedSDK.zip
      SIMPLE.DOS.SDK.gz
        SIMPLE.DOS.SDK
          (DOS disk image)

    zip4.zip
      zip3.zip
        zip2.zip
          zip1.zip
            (hello.txt)
      zip3a.zip
        zip2a.zip
          zip1a.zip
            (hello-a.txt)
