using System;
using System.Threading;
using FluentFTP;

namespace Ftpush.Internal {
    public class FtpClientLease : IDisposable {
        private readonly Lazy<FtpClient> _lazy;
        private readonly Action<Lazy<FtpClient>> _release;
        private int _released;

        public FtpClientLease(Lazy<FtpClient> lazy, Action<Lazy<FtpClient>> release) {
            _lazy = lazy;
            _release = release;
        }

        public FtpClient Client => _lazy.Value;

        public void Dispose() {
            if (Interlocked.CompareExchange(ref _released, 1, 0) == 0)
                _release(_lazy);
        }
    }
}
