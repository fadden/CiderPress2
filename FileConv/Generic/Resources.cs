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
using DiskArc;

namespace FileConv.Generic {
    public class Resources : Converter {
        public const string TAG = "rsrc";
        public const string LABEL = "Resource listing";
        public const string DESCRIPTION = "Generates a listing of the file's resource fork.";
        public const string DISCRIMINATOR = "any file with a resource fork.";
        public override string Tag => TAG;
        public override string Label => LABEL;
        public override string Description => DESCRIPTION;
        public override string Discriminator => DISCRIMINATOR;


        private Resources() { }

        public Resources(FileAttribs attrs, Stream? dataStream, Stream? rsrcStream,
                ResourceMgr? resMgr, ConvFlags convFlags, AppHook appHook)
                : base(attrs, dataStream, rsrcStream, resMgr, convFlags, appHook) {
            Applic = TestApplicability();
        }
         
        protected override Applicability TestApplicability() {
            if (RsrcStream == null) {
                return Applicability.Not;
            } else {
                // We never offer this up as a multiple-choice option.
                return Applicability.NotSelectable;
            }
        }

        public override Type GetExpectedType(Dictionary<string, string> options) {
            return typeof(SimpleText);
        }

        public override IConvOutput ConvertFile(Dictionary<string, string> options) {
            IConvOutput? output = FormatResources(options);
            if (output == null) {
                return new ErrorText("ERROR");
            } else {
                return output;
            }
        }
    }
}
