/*
 * Copyright 2023 faddenSoft
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

using CommonUtil;

namespace FileConv {
    /// <summary>
    /// SimpleText subclass that should be recognized as containing an error message.  This
    /// offers no additional functionality.  It's just a bit more obvious than a boolean "this
    /// is an error message" property.
    /// </summary>
    public class ErrorText : SimpleText {
        public ErrorText(string msg) : base(msg) { }
        public ErrorText(string msg, Notes notes) : base(msg) {
            Notes.MergeFrom(notes);
        }
    }
}
