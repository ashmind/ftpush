using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CommandLine;

namespace Ftpush {
    public class Arguments {
        [Option('t', "to", HelpText = "FTP URL (ftp://host/path).", Required = true)]
        public string FtpUrl { get; set; }

        [Option('u', "username", HelpText = "FTP user name.", Required = true)]
        public string FtpUserName { get; set; }

        [Option('p', "passvar", HelpText = "Name of env variable with FTP password.", Required = true)]
        public string FtpPasswordVariableName { get; set; }

        [Option('f', "from", HelpText = "Source directory.", Required = true)]
        public string SourcePath { get; set; }

        [Option('v', "verbose", HelpText = "Whether to print tracing information.", DefaultValue = false)]
        public bool Verbose { get; set; }
    }
}
