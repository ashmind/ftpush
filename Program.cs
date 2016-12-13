using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using FluentFTP;

namespace Ftpush {
    public static class Program {
        public static int Main(string[] args) {
            try {
                var arguments = new Arguments();
                if (!CommandLine.Parser.Default.ParseArgumentsStrict(args, arguments))
                    return -1;

                Main(arguments);
                return 0;
            }
            catch (Exception ex) {
                var ftpException = ex as FtpCommandException;
                if (ftpException != null)
                    FluentConsole.Red.Line($"{ftpException.CompletionCode} {ftpException.Message}");

                FluentConsole.Red.Text(ex);
                return ex.HResult;
            }
        }

        private static void Main(Arguments args) {
            if (args.Verbose)
                FtpTrace.AddListener(new ConsoleTraceListener());

            var ftpUrl = new Uri(args.FtpUrl);
            var password = Environment.GetEnvironmentVariable(args.FtpPasswordVariableName, EnvironmentVariableTarget.Process);
            if (string.IsNullOrEmpty(password))
                throw new Exception($"Password env variable '{args.FtpPasswordVariableName}' is not set for the current process (user/machine vars are not looked at for security reasons).");

            using (var client = new FtpClient()) {
                client.DataConnectionType = FtpDataConnectionType.AutoActive;
                client.Host = ftpUrl.Host;
                client.Credentials = new NetworkCredential(args.FtpUserName, password);
                client.Connect();

                var basePath = ftpUrl.LocalPath;

                FluentConsole.Green.Line("Connected to {0}.", ftpUrl.Host);
                Process.SynchronizeDirectory(client, new DirectoryInfo(args.SourcePath), basePath);
            }
        }
    }
}
