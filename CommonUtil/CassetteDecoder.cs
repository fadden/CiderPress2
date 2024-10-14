/*
 * Copyright 2024 faddenSoft
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.Diagnostics;

namespace CommonUtil {
    /// <summary>
    /// Decodes (demodulates) a sound file with Apple II cassette data into chunks.
    /// The details of the format, and some notes on the approach we use here, are described in
    /// DiskArc/Arc/AudioRecording-notes.md.
    /// </summary>
    /// <remarks>
    /// <para>This is a fairly direct port of the code in the original CiderPress.  See the
    /// <see href="https://github.com/fadden/ciderpress/blob/master/app/CassetteDialog.cpp">CassetteDialog.[h,cpp]</see>
    /// implementation.</para>
    /// <para>Two basic algorithms are used: measuring the distance between zero-crossings, and
    /// measuring the distance between peaks.  The former is what the Apple II does, and is
    /// generally reliable, but it fails if the cassette has developed a DC bias or a large
    /// low-frequency distortion that pushes the signal off the zero line.  Measuring between
    /// peaks is a little trickier because some recordings have two peaks (one lower than the
    /// other) instead of one, so simply watching for a change in direction doesn't work.</para>
    /// </remarks>
    public class CassetteDecoder {
        /// <summary>
        /// Algorithm to use when analyzing samples.
        /// </summary>
        public enum Algorithm {
            Unknown = 0, Zero, SharpPeak, RoundPeak, ShallowPeak
        }

        /// <summary>
        /// One chunk of data decoded from the sound stream.
        /// </summary>
        public class Chunk {
            /// <summary>
            /// Full file data, minus the final (checksum) byte.
            /// </summary>
            public byte[] Data { get; private set; }

            /// <summary>
            /// Checksum value read from the data stream.
            /// </summary>
            public byte ReadChecksum { get; private set; }

            /// <summary>
            /// Checksum value calculated from the data stream.  This will be 0x00 if all is
            /// well.  If not, the data may be damaged (or, rarely, copy-protected).
            /// </summary>
            public byte CalcChecksum { get; private set; }

            /// <summary>
            /// True if the checksum did not match.
            /// </summary>
            public bool BadChecksum => CalcChecksum != 0;

            /// <summary>
            /// True if the file didn't end on a byte boundary.  This suggests that a burst
            /// of noise ended the process prematurely.
            /// </summary>
            public bool BadEnd { get; private set; }

            /// <summary>
            /// Sample number of start of chunk in sound file.  Useful when examining the sound
            /// file with an editor.
            /// </summary>
            public int StartSample { get; private set; }

            /// <summary>
            /// First sample past end of chunk in sound file.
            /// </summary>
            public int EndSample { get; private set; }

            public Chunk(byte[] data, byte readChecksum, byte calcChecksum, bool badEnd,
                    int startSample, int endSample) {
                Data = data;
                ReadChecksum = readChecksum;
                CalcChecksum = calcChecksum;
                BadEnd = badEnd;
                StartSample = startSample;
                EndSample = endSample;
            }

            public override string ToString() {
                return Data.Length + " bytes, ex-sum=$" + ReadChecksum.ToString("x2") +
                    " act-sum=$" + CalcChecksum.ToString("x2") + ", badEnd=" + BadEnd +
                    " start=" + StartSample +", end=" + EndSample;
            }
        }

        //
        // Innards.
        //

        private WAVFile mWavFile;
        private Algorithm mAlg;

        private List<Chunk> mChunks = new List<Chunk>();

        private const int BUFFER_SIZE = 65536;      // must be mult of 4, in case of 16-bit stereo
        private const int MAX_FILE_LEN = 512 * 1024;    // 512KB is a ~45 minute recording

        // Width of 1/2 cycle in 770Hz lead-in (1000000 / 770 / 2).
        private const float LEAD_IN_HALF_WIDTH_USEC = 650.0f;
        // Max error when detecting 770Hz lead-in, allows [542,758] usec
        private const float LEAD_IN_MAX_ERROR_USEC = 108.0f;
        // Width of 1/2 cycle of "short 0".
        private const float SHORT_ZERO_HALF_WIDTH_USEC = 200.0f;
        // Max error when detecting short 0 (allows [50,350] usec).
        private const float SHORT_ZERO_MAX_ERROR_USEC = 150.0f;
        // Width of 1/2 cycle of '0' (2kHz).
        private const float ZERO_HALF_WIDTH_USEC = 250.0f;
        // Max error when detecting '0'.
        private const float ZERO_MAX_ERROR_USEC = 94.0f;
        // Width of 1/2 cycle of '1' (1kHz).
        private const float ONE_HALF_WIDTH_USEC = 500.0f;
        // Max error when detecting '1'.
        private const float ONE_MAX_ERROR_USEC = 94.0f;
        // After this many 770Hz half-cycles, start looking for short 0.
        private const int LEAD_IN_HALF_CYC_THRESHOLD = 1540;    // 1 second

        // Amplitude must change by this much before we switch out of "peak" mode.
        private const float PEAK_THRESHOLD = 0.2f;              // 10%
        // Amplitude must change by at least this much to stay in "transition" mode.
        private const float TRANS_MIN_DELTA = 0.02f;            // 1%
        // TRANS_MIN_DELTA happens over this range (1 sample at 22.05kHz).
        private const float TRANS_DELTA_BASE_USEC = 43.35f;

        // Decoder state.
        private enum State {
            Unknown = 0,
            ScanFor770Start,
            Scanning770,
            ScanForShort0,
            Short0B,
            ReadData,
            EndReached
        }

        // Bit decode state.
        private enum Mode {
            Unknown = 0,
            Initial0,
            Initial1,
            InTransition,
            AtPeak,
            Running
        }

        // Scan state.
        private State mState;
        private Mode mMode;
        private bool mPositive;

        private int mLastZeroIndex;
        private int mLastPeakStartIndex;
        private float mLastPeakStartValue;

        private float mPrevSample;

        private float mHalfCycleWidthUsec;
        private int mNum770;                // # of consecutive 770Hz cycles
        private int mDataStart;
        private int mDataEnd;

        private float mUsecPerSample;       // constant


        /// <summary>
        /// Private constructor.
        /// </summary>
        private CassetteDecoder(WAVFile waveFile, Algorithm alg) {
            mWavFile = waveFile;
            mAlg = alg;
        }

        /// <summary>
        /// Decodes a stream of audio samples into data chunks.
        /// </summary>
        /// <param name="wavFile">Processed RIFF file with WAVE data (.wav).</param>
        /// <param name="firstOnly">If true, stop when the first good chunk is found.</param>
        /// <returns>List of chunks, in the order in which they appear in the sound file.</returns>
        public static List<Chunk> DecodeFile(WAVFile wavFile, Algorithm alg, bool firstOnly) {
            CassetteDecoder decoder = new CassetteDecoder(wavFile, alg);
            decoder.Scan(firstOnly);
            return decoder.mChunks;
        }

        /// <summary>
        /// Scans the contents of the audio file, generating file chunks as it goes.
        /// </summary>
        private void Scan(bool firstOnly) {
            Debug.Assert(mWavFile.FormatTag == WAVFile.WAVE_FORMAT_PCM);
            Debug.WriteLine("Scanning file: " + mWavFile.GetInfoString());

            MemoryStream outStream = new MemoryStream();
            float[] sampleBuf = new float[16384];
            mUsecPerSample = 1000000.0f / mWavFile.SamplesPerSec;

            bool doInit = true;
            byte checksum = 0;
            int bitAcc = 0;
            int curSampleIndex = 0;

            mWavFile.SeekToStart();
            while (true) {
                int startSampleIndex = -1;
                int count = mWavFile.GetSamples(sampleBuf, 0);
                if (count == 0) {
                    break;      // EOF reached
                } else if (count == -1) {
                    // Whatever caused this should have been caught earlier.
                    throw new NotSupportedException("unable to get samples");
                }

                for (int i = 0; i < count; i++) {
                    if (doInit) {
                        mState = State.ScanFor770Start;
                        mMode = Mode.Initial0;
                        mPositive = false;
                        checksum = 0xff;
                        bitAcc = 1;
                        outStream.SetLength(0);
                        doInit = false;
                    }

                    int bitVal;
                    bool gotBit;
                    switch (mAlg) {
                        case Algorithm.Zero:
                            gotBit =
                                ProcessSampleZero(sampleBuf[i], curSampleIndex + i, out bitVal);
                            break;
                        case Algorithm.SharpPeak:
                        case Algorithm.RoundPeak:
                        case Algorithm.ShallowPeak:
                            gotBit =
                                ProcessSamplePeak(sampleBuf[i], curSampleIndex + i, out bitVal);
                            break;
                        default:
                            throw new NotImplementedException("what is " + mAlg);
                    }
                    if (gotBit) {
                        // Shift the bit into the byte, and output it when we get 8 bits.
                        Debug.Assert(bitVal == 0 || bitVal == 1);
                        bitAcc = (bitAcc << 1) | bitVal;
                        if (bitAcc > 0xff) {
                            outStream.WriteByte((byte)bitAcc);
                            checksum ^= (byte)bitAcc;
                            bitAcc = 1;
                        }

                        if (outStream.Length > MAX_FILE_LEN) {
                            // Something must be off.
                            Debug.WriteLine("Found enormous file on cassette, abandoning");
                            mState = State.EndReached;
                        }
                    }
                    if (mState == State.Scanning770 && startSampleIndex < 0) {
                        startSampleIndex = curSampleIndex + i;
                    }
                    if (mState == State.EndReached) {
                        // Copy data and create the chunk object.
                        Chunk chunk;
                        if (outStream.Length == 0) {
                            chunk = new Chunk(new byte[0], 0x00, 0xff, bitAcc != 1,
                                mDataStart, mDataEnd);
                        } else {
                            byte[] fileData = new byte[outStream.Length - 1];
                            outStream.Position = 0;
                            outStream.ReadExactly(fileData, 0, fileData.Length);
                            int readChecksum = outStream.ReadByte();
                            chunk = new Chunk(fileData, (byte)readChecksum, checksum,
                                bitAcc != 1, mDataStart, mDataEnd);
                        }
                        Debug.WriteLine("Found: " + chunk.ToString());
                        mChunks.Add(chunk);
                        doInit = true;
                        if (firstOnly) {
                            break;
                        }
                    }
                }

                curSampleIndex += count;

                if (firstOnly && mChunks.Count > 0) {
                    break;
                }
            }

            switch (mState) {
                case State.ScanFor770Start:
                case State.Scanning770:
                case State.EndReached:
                    Debug.WriteLine("Scan ended in normal state: " + mState);
                    break;
                default:
                    Debug.WriteLine("Scan ended in unexpected state: " + mState);
                    break;
            }
        }

        /// <summary>
        /// Implements the zero-crossing algorithm.
        /// </summary>
        private bool ProcessSampleZero(float sample, int sampleIndex, out int bitVal) {
            bitVal = 0;

            // Analyze the mode, changing to a new one when appropriate.
            bool crossedZero = false;
            switch (mMode) {
                case Mode.Initial0:
                    Debug.Assert(mState == State.ScanFor770Start);
                    mMode = Mode.Running;
                    break;
                case Mode.Running:
                    if (mPrevSample < 0.0f && sample >= 0.0f ||
                            mPrevSample >= 0.0f && sample < 0.0f) {
                        crossedZero = true;
                    }
                    break;
                default:
                    Debug.Assert(false);
                    break;
            }


            // Deal with a zero-crossing.
            //
            // We currently just grab the first point after we cross.  We should
            // be grabbing the closest point or interpolating across.
            bool emitBit = false;
            if (crossedZero) {
                float halfCycleUsec;
                int bias;

                if (Math.Abs(mPrevSample) < Math.Abs(sample)) {
                    bias = -1;      // previous sample was closer to zero point
                } else {
                    bias = 0;       // current sample is closer
                }

                // Delta time for zero-to-zero (half cycle).
                int timeDelta = (sampleIndex + bias) - mLastZeroIndex;

                halfCycleUsec = timeDelta * mUsecPerSample;
                emitBit = UpdateState(sampleIndex + bias, halfCycleUsec, out bitVal);
                mLastZeroIndex = sampleIndex + bias;
            }

            // Remember this sample for the next go-round.
            mPrevSample = sample;

            return emitBit;
        }

        /// <summary>
        /// Implements the peak-to-peak algorithm.
        /// </summary>
        private bool ProcessSamplePeak(float sample, int sampleIndex, out int bitVal) {
            bitVal = 0;
            float ampDelta;
            float transitionLimit;
            bool hitPeak = false;
            bool emitBit = false;

            // Analyze the mode, changing to a new one when appropriate.
            switch (mMode) {
                case Mode.Initial0:
                    Debug.Assert(mState == State.ScanFor770Start);
                    mMode = Mode.Initial1;
                    break;
                case Mode.Initial1:
                    Debug.Assert(mState == State.ScanFor770Start);
                    mPositive = (sample >= mPrevSample);
                    mMode = Mode.InTransition;
                    // Set these up with something reasonable.
                    mLastPeakStartIndex = sampleIndex;
                    mLastPeakStartValue = sample;
                    break;
                case Mode.InTransition:
                    // Stay in this state until two adjacent samples are very close in
                    // amplitude (or we change direction).  We need to adjust our amplitude
                    // threshold based on sampling frequency, or at higher sample rates
                    // we're going to think everything is a transition.
                    //
                    // The approach here is overly simplistic, and is prone to failure
                    // when the sampling rate is high, especially with 8-bit samples
                    // or sounds cards that don't really have 16-bit resolution.  The
                    // proper way to do this is to keep a short history, and evaluate
                    // the delta amplitude over longer periods.  [At this point I'd
                    // rather just tell people to record at 22.05kHz.]
                    //
                    // Set the "hitPeak" flag and handle the consequence below.
                    if (mAlg == Algorithm.RoundPeak) {
                        transitionLimit = TRANS_MIN_DELTA * (mUsecPerSample / TRANS_DELTA_BASE_USEC);
                    } else {
                        transitionLimit = 0.0f;
                    }

                    if (mPositive) {
                        if (sample < mPrevSample + transitionLimit) {
                            mMode = Mode.AtPeak;
                            hitPeak = true;
                        }
                    } else {
                        if (sample > mPrevSample - transitionLimit) {
                            mMode = Mode.AtPeak;
                            hitPeak = true;
                        }
                    }
                    break;
                case Mode.AtPeak:
                    transitionLimit = PEAK_THRESHOLD;
                    if (mAlg == Algorithm.ShallowPeak) {
                        transitionLimit /= 4.0f;
                    }

                    ampDelta = mLastPeakStartValue - sample;
                    if (ampDelta < 0) {
                        ampDelta = -ampDelta;
                    }
                    if (ampDelta > transitionLimit) {
                        if (sample >= mLastPeakStartValue) {
                            mPositive = true;       // going up
                        } else {
                            mPositive = false;      // going down
                        }
                        // Mark the end of the peak; could be same as start of peak.
                        mMode = Mode.InTransition;
                    }
                    break;
                default:
                    throw new Exception("Bad mode " + mMode);
            }

            // If we hit "peak" criteria, we regard the *previous* sample as the
            // peak.  This is very important for lower sampling rates (e.g. 8kHz).
            if (hitPeak) {
                // Delta time for peak-to-peak (half cycle).
                int timeDelta = (sampleIndex - 1) - mLastPeakStartIndex;
                // Amplitude peak-to-peak.
                ampDelta = mLastPeakStartValue - mPrevSample;
                if (ampDelta < 0) {
                    ampDelta = -ampDelta;
                }

                float halfCycleUsec = timeDelta * mUsecPerSample;

                emitBit = UpdateState(sampleIndex - 1, halfCycleUsec, out bitVal);

                // Set the "peak start" values.
                mLastPeakStartIndex = sampleIndex - 1;
                mLastPeakStartValue = mPrevSample;
            }

            // Remember this sample for the next go-round.
            mPrevSample = sample;

            return emitBit;
        }

        /// <summary>
        /// Updates the state every half-cycle.
        /// </summary>
        /// <param name="sampleIndex">Index of current sample.</param>
        /// <param name="halfCycleUsec">Width, in usec, of current half-cycle.</param>
        /// <param name="bitVal">Result: bit value we read (when returning true).</param>
        /// <returns>True if we want to output a bit.</returns>
        private bool UpdateState(int sampleIndex, float halfCycleUsec, out int bitVal) {
            bitVal = 0;
            bool emitBit = false;

            float fullCycleUsec;
            if (mHalfCycleWidthUsec != 0.0f) {
                fullCycleUsec = halfCycleUsec + mHalfCycleWidthUsec;
            } else {
                fullCycleUsec = 0.0f;       // only have first half
            }

            switch (mState) {
                case State.ScanFor770Start:
                    // Watch for a cycle of the appropriate length.
                    if (fullCycleUsec != 0.0f &&
                            fullCycleUsec > LEAD_IN_HALF_WIDTH_USEC * 2.0f - LEAD_IN_MAX_ERROR_USEC * 2.0f &&
                            fullCycleUsec < LEAD_IN_HALF_WIDTH_USEC * 2.0f + LEAD_IN_MAX_ERROR_USEC * 2.0f) {
                        // Now scanning 770Hz samples.
                        mState = State.Scanning770;
                        mNum770 = 1;
                    }
                    break;
                case State.Scanning770:
                    // Count up the 770Hz cycles.
                    if (fullCycleUsec != 0.0f &&
                            fullCycleUsec > LEAD_IN_HALF_WIDTH_USEC * 2.0f - LEAD_IN_MAX_ERROR_USEC * 2.0f &&
                            fullCycleUsec < LEAD_IN_HALF_WIDTH_USEC * 2.0f + LEAD_IN_MAX_ERROR_USEC * 2.0f) {
                        mNum770++;
                        if (mNum770 > LEAD_IN_HALF_CYC_THRESHOLD / 2) {
                            // Looks like a solid tone, advance to next phase.
                            mState = State.ScanForShort0;
                            Debug.WriteLine("# looking for short 0");
                        }
                    } else if (fullCycleUsec != 0.0f) {
                        // Pattern lost, reset.
                        if (mNum770 > 5) {
                            Debug.WriteLine("# lost 770 at " + sampleIndex + " width=" + fullCycleUsec +
                                " count=" + mNum770);
                        }
                        mState = State.ScanFor770Start;
                    }
                    break;
                case State.ScanForShort0:
                    // Found what looks like a 770Hz field, find the short 0.
                    if (halfCycleUsec > SHORT_ZERO_HALF_WIDTH_USEC - SHORT_ZERO_MAX_ERROR_USEC &&
                            halfCycleUsec < SHORT_ZERO_HALF_WIDTH_USEC + SHORT_ZERO_MAX_ERROR_USEC) {
                        Debug.WriteLine("# found short zero (half=" + halfCycleUsec + ") at " +
                            sampleIndex + " after " + mNum770 + " 770s");
                        mState = State.Short0B;
                        // Make sure we treat current sample as first half.
                        mHalfCycleWidthUsec = 0.0f;
                    } else if (fullCycleUsec != 0.0f &&
                            fullCycleUsec > LEAD_IN_HALF_WIDTH_USEC * 2.0f - LEAD_IN_MAX_ERROR_USEC * 2.0f &&
                            fullCycleUsec < LEAD_IN_HALF_WIDTH_USEC * 2.0f + LEAD_IN_MAX_ERROR_USEC * 2.0f) {
                        // Still reading 770Hz cycles.
                        mNum770++;
                    } else if (fullCycleUsec != 0.0f) {
                        // Full cycle of the wrong size, we've lost it.
                        Debug.WriteLine("# lost 770 at " + sampleIndex + " width=" + fullCycleUsec +
                            " count=" + mNum770);
                        mState = State.ScanFor770Start;
                    }
                    break;
                case State.Short0B:
                    // Pick up the second half of the start cycle.
                    Debug.Assert(fullCycleUsec != 0.0f);
                    if (fullCycleUsec > (SHORT_ZERO_HALF_WIDTH_USEC + ZERO_HALF_WIDTH_USEC) - ZERO_MAX_ERROR_USEC * 2.0f &&
                            fullCycleUsec < (SHORT_ZERO_HALF_WIDTH_USEC + ZERO_HALF_WIDTH_USEC) + ZERO_MAX_ERROR_USEC * 2.0f) {
                        // As expected.
                        Debug.WriteLine("# found 0B " + halfCycleUsec + " (total " + fullCycleUsec +
                            "), advancing to read data state");
                        mDataStart = sampleIndex;
                        mState = State.ReadData;
                    } else {
                        // Must be a false-positive at end of tone.
                        Debug.WriteLine("# didn't find post-short-0 value (half=" +
                            mHalfCycleWidthUsec + " + " + halfCycleUsec + ")");
                        mState = State.ScanFor770Start;
                    }
                    break;
                case State.ReadData:
                    // Check width of full cycle; don't double error allowance.
                    if (fullCycleUsec != 0.0f) {
                        if (fullCycleUsec > ZERO_HALF_WIDTH_USEC * 2 - ZERO_MAX_ERROR_USEC * 2 &&
                                fullCycleUsec < ZERO_HALF_WIDTH_USEC * 2 + ZERO_MAX_ERROR_USEC * 2) {
                            bitVal = 0;
                            emitBit = true;
                        } else
                        if (fullCycleUsec > ONE_HALF_WIDTH_USEC * 2 - ONE_MAX_ERROR_USEC * 2 &&
                                fullCycleUsec < ONE_HALF_WIDTH_USEC * 2 + ONE_MAX_ERROR_USEC * 2) {
                            bitVal = 1;
                            emitBit = true;
                        } else {
                            // Bad cycle, assume end reached.
                            Debug.WriteLine("# bad full cycle time " + fullCycleUsec +
                                " in data at " + sampleIndex + ", bailing");
                            mDataEnd = sampleIndex;
                            mState = State.EndReached;
                        }
                    }
                    break;
                default:
                    throw new Exception("bad state " + mState);
            }

            // Save the half-cycle stats.
            if (mHalfCycleWidthUsec == 0.0f) {
                mHalfCycleWidthUsec = halfCycleUsec;
            } else {
                mHalfCycleWidthUsec = 0.0f;
            }
            return emitBit;
        }
    }
}
