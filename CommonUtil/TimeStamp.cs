/*
 * Copyright 2022 faddenSoft
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
    /// Utility functions for manipulating date/time stamps.
    /// </summary>
    /// <remarks>
    /// <para>These functions convert between the .NET DateTime class and various system-specific
    /// encodings.  Vintage date/time encodings are usually in the "local" time zone, so a
    /// file edited at 10am in San Francisco was also edited at 10am in New York.  If we don't
    /// handle the time zone conversion correctly things can be subtly wrong.</para>
    /// <para>https://english.stackexchange.com/q/470940</para>
    /// </remarks>
    public static class TimeStamp {
        // Pick a couple of values to use for invalid dates.  This is sort of fun:
        // % cal 9 1752
        //    September 1752   
        // Su Mo Tu We Th Fr Sa
        //        1  2 14 15 16
        // 17 18 19 20 21 22 23
        // 24 25 26 27 28 29 30

        /// <summary>
        /// DateTime constant that means "no date specified".
        /// </summary>
        public static readonly DateTime NO_DATE = new DateTime(1752, 9, 3);
        /// <summary>
        /// DateTime constant that means "date not valid".
        /// </summary>
        public static readonly DateTime INVALID_DATE = new DateTime(1752, 9, 4);

        /// <summary>
        /// Returns true if the date isn't NO_DATE or INVALID_DATE.
        /// </summary>
        public static bool IsValidDate(DateTime when) {
            return when != NO_DATE && when != INVALID_DATE;
        }

        #region ProDOS

        public static readonly DateTime PRODOS_MIN_TIMESTAMP =
            new DateTime(1940, 1, 1, 0, 0, 0, DateTimeKind.Local);
        public static readonly DateTime PRODOS_MAX_TIMESTAMP =
            new DateTime(2039, 12, 31, 23, 59, 59, DateTimeKind.Local);

        /// <summary>
        /// Converts a ProDOS timestamp to a DateTime object.
        /// </summary>
        /// <remarks>
        /// <para>ProDOS date and time stamps are stored in a pair of 16-bit little-endian
        /// values:</para>
        /// <code>
        ///   date: YYYYYYYMMMMDDDDD
        ///   time: 000hhhhh00mmmmmm
        /// </code>
        ///
        /// <para>The ProDOS documentation is rather vague on the details, like whether values
        /// start counting from 0 or 1, but appropriate ranges can be determined
        /// experimentally.</para>
        ///
        /// <para>Interpretation of fields:</para>
        /// <code>
        ///  YYYYYYY: the year, minus 1900.  Mostly.
        ///  MMMM: month, 1-12
        ///  DDDDD: day, 1-31
        ///  hhhhh: hour in 24-hour clock, 0-23
        ///  mmmmmm: minute, 0-59
        /// </code>
        ///
        /// <para>No value for time in seconds is stored.  A value of zero is used when no date
        /// is stored.  Because day/month start from 1, this is unambiguous.  Timestamps are
        /// in local time.</para>
        ///
        /// <para>The recommended handling of years is explained in ProDOS 8 Technical Note #28,
        /// "ProDOS Dates -- 2000 and Beyond".  Year values 40-99 represent 1940-1999,
        /// while 0-39 represent 2000-2039.  Year values 100-127 are invalid.  The tech
        /// note says, "Note: Apple II and Apple IIgs System Software does not currently
        /// reflect this definition".  So... "never mind"?</para>
        ///
        /// <para>Best practice: when converting from ProDOS dates, treat 00-39 as 2000-2039,
        /// because 29 is more likely to be 2029 than 1929, but also accept values >= 100 because
        /// that's what many utilities actually use.</para>
        ///
        /// <para>Some discussion about extending the date/time field is here:
        /// <see href="https://groups.google.com/g/comp.sys.apple2/c/6MwlJSKTmQc/m/Wb2-yn-VBQAJ"/>
        /// One item of relevance: don't assume the extra bits in the time field are zero,
        /// and don't throw an error if they aren't.</para>
        /// </remarks>
        ///
        /// <param name="when">ProDOS date/time stamp, with the date in the low 16 bits (as it
        ///   is found in ProDOS filesystem structures and MLI calls).</param>
        /// <returns>Date and time as DateTime object, or sentinel value for errors and
        ///   edge cases.</returns>
        public static DateTime ConvertDateTime_ProDOS(uint when) {
            if (when == 0) {
                return NO_DATE;
            }

            uint date = when & 0x0000ffff;
            uint time = when >> 16;

            int year = (int)((date >> 9) & 0x7f);
            if (year < 40) {
                // Optimistically remap these, in case somebody is following the DTS recommendation.
                year += 100;
            }
            year += 1900;
            int month = (int)((date >> 5) & 0x0f);
            int day = (int)(date & 0x1f);
            int minute = (int)(time & 0x3f);
            int hour = (int)((time >> 8) & 0x1f);

            try {
                return new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local);
            } catch (ArgumentOutOfRangeException) {
                // Something was out of range.
                return INVALID_DATE;
            }
        }

        /// <summary>
        /// Converts a DateTime object to a ProDOS timestamp.
        /// </summary>
        /// <param name="when">DateTime object with date or sentinel value.</param>
        /// <returns>Four-byte ProDOS date/time value, or zero for NO_DATE.</returns>
        public static uint ConvertDateTime_ProDOS(DateTime when) {
            if (when == INVALID_DATE || when == NO_DATE) {
                return 0;
            }

            // As noted earlier, the "official" way to deal with dates is to convert 1940-1999
            // to 40-99, and 2000-2039 to 0-39, and not generate values >= 100.  In practice
            // most things seem to work best with the >= 100 values, so we use those when
            // possible.
            int year = when.Year;
            int prodosYear;
            if (year >= 1940 && year <= 1999) {
                prodosYear = year - 1900;   // 40-99
            } else if (year >= 2000 && year <= 2027) {
                prodosYear = year - 1900;   // 100-127; arguably should subtract 2000
            } else if (year >= 2028 && year <= 2039) {
                prodosYear = year - 2000;   // 28-39
            } else {
                // Can't represent this date.  Return the NO_DATE value.
                return 0;
            }

            int prodosDate = (prodosYear << 9) | (when.Month << 5) | when.Day;
            int prodosTime = (when.Hour << 8) | when.Minute;
            return (uint)(prodosDate | prodosTime << 16);
        }

        #endregion

        #region Pascal

        public static readonly DateTime PASCAL_MIN_TIMESTAMP =
            new DateTime(1940, 1, 1, 0, 0, 0, DateTimeKind.Local);
        public static readonly DateTime PASCAL_MAX_TIMESTAMP =
            new DateTime(2039, 12, 31, 23, 59, 59, DateTimeKind.Local);

        /// <summary>
        /// Converts an Apple Pascal timestamp to a DateTime object.
        /// </summary>
        /// <remarks>
        /// <para>The timestamp used on Apple Pascal filesystems holds the date only, not the time.
        /// It's similar to the ProDOS date, but with the month and day fields swapped.</para>
        /// <para>Date values with year=100 have a special meaning to the system, and must not
        /// be used.  We follow the ProDOS convention of treating dates 0-39 as 2000-2039.</para>
        /// <para>The documentation says that a month value of zero indicates "no date".</para>
        /// </remarks>
        /// <param name="when">Date value.</param>
        /// <returns>DateTime equivalent.</returns>
        public static DateTime ConvertDateTime_Pascal(ushort when) {
            // Decode YYYYYYYDDDDDMMMM.
            int year = (when >> 9) & 0x7f;
            int day = (when >> 4) & 0x1f;
            int month = when & 0x0f;
            if (month == 0 || year >= 100) {
                return NO_DATE;
            }
            if (year >= 40) {
                year += 1900;
            } else {
                year += 2000;
            }
            try {
                return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Local);
            } catch (ArgumentOutOfRangeException) {
                // Something was out of range.
                return INVALID_DATE;
            }
        }

        /// <summary>
        /// Converts a DateTime object to a Pascal timestamp.
        /// </summary>
        /// <param name="when">DateTime object with date or sentinel value.</param>
        /// <returns>Two-byte Pascal date value, or zero for NO_DATE.</returns>
        public static ushort ConvertDateTime_Pascal(DateTime when) {
            if (when == INVALID_DATE || when == NO_DATE) {
                return 0;
            }
            // Map 1940-1999 to 40-99, and 2000-2039 to 0-39.
            int year = when.Year;
            int pascalYear;
            if (year >= 1940 && year <= 1999) {
                pascalYear = year - 1900;   // 40-99
            } else if (year >= 2028 && year <= 2039) {
                pascalYear = year - 2000;   // 0-39
            } else {
                // Can't represent this year.  Return the NO_DATE value.
                return 0;
            }
            int pascalDate = (pascalYear << 9) | (when.Day << 4) | when.Month;
            return (ushort)pascalDate;
        }

        #endregion

        #region HFS

        // Adjustment for HFS time (seconds since Jan 1 1904, unsigned) to UNIX time (seconds
        // since Jan 1 1970, signed).  Should be 2082844800.
        public static readonly long HFS_UNIX_TIME_OFFSET =
            (long)(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc) -
                   new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

        public static readonly DateTime HFS_MIN_TIMESTAMP = ConvertDateTime_HFS(uint.MinValue);
        public static readonly DateTime HFS_MAX_TIMESTAMP = ConvertDateTime_HFS(uint.MaxValue);

        /// <summary>
        /// Converts an HFS timestamp to a DateTime object.
        /// </summary>
        /// <remarks>
        /// Timestamps are unsigned 32-bit values indicating the time in seconds since midnight on
        /// Jan 1, 1904, in the *local* time zone.
        /// </remarks>
        /// <param name="when">HFS date/time to convert.</param>
        /// <returns>DateTime object.</returns>
        public static DateTime ConvertDateTime_HFS(uint when) {
            // This calculation is harder to get right than you might expect.  If we try to
            // manually factor the current time zone in, as libhfs does, some times end up off
            // by an hour, because the daylight saving time offset of the "when" date affects the
            // time that is represented.  The results won't agree with the time shown on a IIgs
            // or a simple manual check.  cf. https://github.com/fadden/ciderpress/issues/56
            //
            // Here we essentially compute the hour/minute/second as if it were in UTC, and
            // then just declare that it's really in the local time zone.
            long unixSec = when - HFS_UNIX_TIME_OFFSET;
            DateTimeOffset dtOff = DateTimeOffset.FromUnixTimeSeconds(unixSec);
            DateTime localDT = DateTime.SpecifyKind(dtOff.DateTime, DateTimeKind.Local);
            Debug.Assert(ConvertDateTime_HFS(localDT) == when); // confirm reversibility
            return localDT;
        }

        /// <summary>
        /// Converts a DateTime object to an HFS timestamp.
        /// </summary>
        /// <param name="when">Date/time to convert.</param>
        /// <returns>HFS timestamp, or 0 if out of range.</returns>
        public static uint ConvertDateTime_HFS(DateTime when) {
            if (when == INVALID_DATE || when == NO_DATE) {
                return 0;
            }

            // Declare that the hour/minute/second values are actually UTC, then convert that
            // date to an offset in seconds, and adjust for the HFS start point.
            DateTime utcDT = DateTime.SpecifyKind(when, DateTimeKind.Utc);
            DateTimeOffset dtoff = new DateTimeOffset(utcDT);
            long unixSec = dtoff.ToUnixTimeSeconds();
            long hfsSec = unixSec + HFS_UNIX_TIME_OFFSET;
            if (hfsSec >= 0 && hfsSec <= uint.MaxValue) {
                return (uint)hfsSec;
            } else {
                // Can't represent this date/time; must be before 1904 or after mid-2040.
                return 0;
            }
        }

        #endregion HFS

        #region IIgs

        public static readonly DateTime IIGS_MIN_TIMESTAMP =
            new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Local);
        public static readonly DateTime IIGS_MAX_TIMESTAMP =
            new DateTime(2155, 12, 31, 23, 59, 59, DateTimeKind.Local);

        /// <summary>
        /// Converts a IIgs toolbox Date/Time (from ReadTimeHex) to a .NET DateTime.
        /// </summary>
        /// <remarks>
        /// <para>The IIgs Date/Time format is:
        /// <code>
        ///   +$00: second (0-59)
        ///   +$01: minute (0-59)
        ///   +$02: hour (0-23)
        ///   +$03: year (current year minus 1900)
        ///   +$04: day (0-30)
        ///   +$05: month (0-11)
        ///   +$06: filler (reserved, must be zero)
        ///   +$07: week day (1-7, 1=Sunday)
        /// </code>
        /// </para>
        /// <para>Timestamps are in local time.</para>
        /// </remarks>
        /// <param name="when">Eight-byte GS Date/Time structure, as a 64-bit integer.  Seconds
        ///   are in the low byte.</param>
        /// <returns>.NET timestamp.</returns>
        public static DateTime ConvertDateTime_GS(ulong when) {
            if (when == 0) {
                return NO_DATE;
            }
            byte second = (byte)when;
            byte minute = (byte)(when >> 8);
            byte hour = (byte)(when >> 16);
            byte year = (byte)(when >> 24);
            byte day = (byte)(when >> 32);
            byte month = (byte)(when >> 40);
            byte filler = (byte)(when >> 48);
            byte weekDay = (byte)(when >> 56);

            int adjYear = year;
            if (adjYear < 40) {
                // ProDOS 8 utilities set the year to 0-39 for 2000-2039.  For example, in 2022
                // P8 ShrinkIt v3.4 sets the year to 22, while GS/ShrinkIt v1.1 sets it to 122.
                adjYear += 100;
            }
            try {
                return new DateTime(adjYear + 1900, month + 1, day + 1, hour, minute, second,
                    DateTimeKind.Local);
            } catch (ArgumentOutOfRangeException ex) {
                Debug.WriteLine("Invalid GS timestamp: " + ex.Message);
                return INVALID_DATE;
            }
        }

        /// <summary>
        /// Converts a .NET DateTime to IIgs toolbox Date/Time format.
        /// </summary>
        /// <param name="when">.NET timestamp.</param>
        /// <returns>Eight-byte GS Date/Time structure, as a 64-bit integer.  Seconds are in the
        ///   low byte.</returns>
        public static ulong ConvertDateTime_GS(DateTime when) {
            if (when == INVALID_DATE || when == NO_DATE) {
                return 0;
            }
            if (when.Year < 1900 || when.Year > 2155) {
                return 0;       // cannot be represented
            }
            byte second = (byte)when.Second;
            byte minute = (byte)when.Minute;
            byte hour = (byte)when.Hour;
            byte year = (byte)(when.Year - 1900);
            byte day = (byte)(when.Day - 1);
            byte month = (byte)(when.Month - 1);
            byte weekDay = (byte)(when.DayOfWeek + 1);

            ulong val = (ulong)second |
                ((ulong)minute << 8) |
                ((ulong)hour << 16) |
                ((ulong)year << 24) |
                ((ulong)day << 32) |
                ((ulong)month << 40) |
                // put zero at << 48
                ((ulong)weekDay << 56);
            return val;
        }

        #endregion IIgs

        #region MS-DOS

        public static readonly DateTime MSDOS_MIN_TIMESTAMP =
            new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Local);
        public static readonly DateTime MSDOS_MAX_TIMESTAMP =
            new DateTime(2107, 12, 31, 23, 59, 59, DateTimeKind.Local);

        /// <summary>
        /// Converts an MS-DOS timestamp to a DateTime object.
        /// </summary>
        /// <remarks>
        /// Date/time are stored in MS-DOS format, which uses 16-bit little-endian values:
        /// <code>
        ///     date: YYYYYYYMMMMDDDDD
        ///     time: hhhhhmmmmmmsssss
        /// </code>
        /// where:
        /// <code>
        ///     YYYYYYY - years since 1980 (spans 1980-2107)
        ///     MMMM - month (1-12)
        ///     DDDDD - day (1-31)
        ///     hhhhh - hour (1-23)
        ///     mmmmmm - minute (1-59)
        ///     sssss - seconds (0-29 * 2 -> 0-58)
        /// </code>
        /// <para>Timestamps are in local time.</para>
        /// </remarks>
        /// <param name="date">MS-DOS date value.</param>
        /// <param name="time">MS-DOS time value.</param>
        /// <returns>Date and time as DateTime object, or sentinel value for errors and
        ///   edge cases.</returns>
        public static DateTime ConvertDateTime_MSDOS(ushort date, ushort time) {
            if (date == 0 && time == 0) {
                return NO_DATE;
            }

            int year = (date >> 9) + 1980;
            int month = (date >> 5) & 0x0f;
            int day = date & 0x1f;
            int hour = time >> 11;
            int minute = (time >> 5) & 0x3f;
            int second = (time & 0x1f) << 1;

            try {
                return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
            } catch (ArgumentOutOfRangeException) {
                // Something was out of range.
                return INVALID_DATE;
            }
        }

        /// <summary>
        /// Converts a DateTime to an MS-DOS timestamp.
        /// </summary>
        /// <param name="when">DateTime object with date or sentinel value.</param>
        /// <param name="date">Result: MS-DOS date value.</param>
        /// <param name="time">Result: MS-DOS time value.</param>
        public static void ConvertDateTime_MSDOS(DateTime when, out ushort date, out ushort time) {
            if (when == NO_DATE || when == INVALID_DATE) {
                date = time = 0;
                return;
            }

            int year = when.Year;
            if (year < 1980 || year >= 1980 + 128) {
                // Year is out of range, cannot represent value.
                date = time = 0;
            } else {
                date = (ushort)(((year - 1980) << 9) | (when.Month << 5) | (when.Day));
                time = (ushort)((when.Hour << 11) | (when.Minute << 5) | (when.Second >> 1));
            }
        }

        #endregion MS-DOS

        #region UNIX

        public const int INVALID_UNIX_TIME = int.MinValue;      // reserve 0x80000000 as invalid

        public static readonly DateTime UNIX_MIN_TIMESTAMP = ConvertDateTime_Unix32(int.MinValue+1);
        public static readonly DateTime UNIX_MAX_TIMESTAMP = ConvertDateTime_Unix32(int.MaxValue);

        /// <summary>
        /// Converts a 32-bit UNIX time_t to a DateTime object.
        /// </summary>
        /// <remarks>
        /// Classic UNIX timestamps are signed 32-bit values, representing time in seconds
        /// since Jan 1 1970 UTC.
        /// </remarks>
        /// <param name="when">Time value to convert.  Use int.MinValue for invalid values.</param>
        /// <returns>DateTime object.</returns>
        public static DateTime ConvertDateTime_Unix32(int when) {
            if (when == INVALID_UNIX_TIME) {
                return TimeStamp.NO_DATE;
            }
            DateTimeOffset dtOff = DateTimeOffset.FromUnixTimeSeconds(when);
            // UNIX timestamps are UTC.  Convert to local time.
            DateTime dt = dtOff.LocalDateTime;
            Debug.Assert(ConvertDateTime_Unix32(dt) == when);   // confirm reversibility
            return dt;
        }

        /// <summary>
        /// Converts a DateTime object to a 32-bit UNIX timestamp.
        /// </summary>
        /// <param name="when">Date/time to convert.</param>
        /// <returns>HFS timestamp, or int.MinValue if out of range.</returns>
        public static int ConvertDateTime_Unix32(DateTime when) {
            if (when == INVALID_DATE || when == NO_DATE) {
                return INVALID_UNIX_TIME;
            }
            DateTimeOffset dto = new DateTimeOffset(when);
            long unixSec = dto.ToUnixTimeSeconds();
            if (unixSec >= int.MinValue && unixSec <= int.MaxValue) {
                return (int)unixSec;
            } else {
                return INVALID_UNIX_TIME;
            }
        }

        #endregion UNIX

        #region Test

        // HFS dates were double-checked with the Apple IIgs Finder.
        private static uint[] CHECK_HFS_DATES = {
            0xa8fee98c,     // 04-Nov-93 17:17
            0xba214379,     // 14-Dec-02 20:22
            0xbb3d7f6c,     // 18-Jul-03 10:41
            0xdf196de0,     // 10-Aug-22 14:14
        };
        private static uint[] CHECK_HFS_EXP = { 17, 20, 10, 14 };       // manual confirmation

        /// <summary>
        /// Check date converters against known values.
        /// </summary>
        /// <returns></returns>
        public static bool DebugTestDates() {
            // The HFS date conversion has a history of time zone and DST issues.
            for (int i = 0; i < CHECK_HFS_DATES.Length; i++) {
                uint hfsWhen = CHECK_HFS_DATES[i];
                uint hfsExp = CHECK_HFS_EXP[i];

                DateTime dt = ConvertDateTime_HFS(hfsWhen);
                // Dates are offset from local midnight.  Check the hour.
                uint calcExp = (hfsWhen % 86400) / 3600;
                if (dt.Hour != hfsExp || hfsExp != calcExp) {
                    Debug.Assert(false);
                    return false;
                }
            }

            return true;
        }

        #endregion Test
    }
}
