# Data Compression Codecs #

All codecs follow the same pattern as System.IO.Compression classes like DeflateStream.  Each
codec is a Stream sub-class that can be read from or written to, but not both at once.

 - When compressing, the application provides a writable stream that will receive the compressed
   output.  It writes the uncompressed input data to the codec stream.  The codec must be closed
   to complete the operation.
 - When decompressing, the application provides a readable stream with the compressed input.
   It reads the uncompressed data from the codec stream, until it receives an end-of-file signal
   (zero-length Read).

The codec Stream is not seekable, and the codecs do not change the position of the input or
output streams.

It's necessary to close the codec when compressing to provide a signal that all input has been
provided.  This allows the codec to flush any pending data.  When expanding, the codec will
know when all data has been decompressed, either by seeing a special symbol in the stream or
by having processed a certain amount of data.

Some compression algorithms, such as Squeeze and NuFX LZW/1, are not fully streamable in one or
both directions.  These will not be able to produce any output until all input has been provided.

If a stream has a built-in checksum, it will be verified automatically, with failures reported
as an InvalidDataException.  Encountering corrupted data while decompressing will also cause
an exception.

## Design Notes ##

My original design used zlib-style buffer management, where a single function is responsible for
feeding input and draining output.  This approach is very flexible, but adds complexity to the
application and the compression codecs.  The streaming approach makes certain features more
complicated, e.g. halting compression as soon as the output exceeds the length of the original
file would require adding another Stream layer to the output, but overall the streaming approach
is simpler.

(It's marginally harder to write generic compression tests, because instead of passing a
resettable codec object around you have to provide a delegate that creates a new codec
instance.  This is a very minor consideration.)

The most efficient approach will always be to present the codec with a pair of memory buffers
that hold all of the data, but that can be problematic for large files.  Very few things in the
Apple II world do not fit comfortably in desktop RAM at this point, but files on an HFS
volume can be very large.
