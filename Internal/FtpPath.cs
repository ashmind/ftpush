namespace Ftpush.Internal {
    public class FtpPath {
        public FtpPath(string name, string absolute, string relative, int depth) {
            Name = name;
            Absolute = absolute;
            Relative = relative;
            Depth = depth;
        }

        public string Name { get; }
        public string Absolute { get; }
        public string Relative { get; }
        public int Depth { get; }

        public FtpPath GetChildPath(string name) {
            return new FtpPath(name, Absolute + "/" + name, Relative + "/" + name, Depth + 1);
        }
    }
}