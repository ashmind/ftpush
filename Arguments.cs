using System.Collections.Generic;
using CommandLine;
using JetBrains.Annotations;

namespace Ftpush {
    public class Arguments {
        [UsedImplicitly(ImplicitUseKindFlags.Assign)]
        [Option('t', "target", HelpText = "FTP URL (ftp://host/path).", Required = true)]
        public string FtpUrl { get; set; }

        [UsedImplicitly(ImplicitUseKindFlags.Assign)]
        [Option('u', "username", HelpText = "FTP user name.", Required = true)]
        public string FtpUserName { get; set; }

        [UsedImplicitly(ImplicitUseKindFlags.Assign)]
        [Option('p', "passvar", HelpText = "Name of env variable with FTP password.", Required = true)]
        public string FtpPasswordVariableName { get; set; }

        [UsedImplicitly(ImplicitUseKindFlags.Assign)]
        [Option('s', "source", HelpText = "Source directory.", Required = true)]
        public string SourcePath { get; set; }

        [UsedImplicitly(ImplicitUseKindFlags.Assign)]
        [OptionArray('x', "exclude", HelpText = "Excluded patterns: those will not be copied from source or deleted from target.")]
        public string[] Excludes { get; set; } = new string[0];

        [UsedImplicitly(ImplicitUseKindFlags.Assign)]
        [Option('v', "verbose", HelpText = "Whether to print tracing information.", DefaultValue = false)]
        public bool Verbose { get; set; }
    }
}
