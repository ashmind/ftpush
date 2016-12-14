using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using FluentFTP;

namespace Ftpush.Internal {
    public class FtpClientPool : IDisposable {
        private readonly BlockingCollection<Lazy<FtpClient>> _available;

        public FtpClientPool(Func<FtpClient> clientFactory, int maxCount) {
            _available = new BlockingCollection<Lazy<FtpClient>>(maxCount);
            for (var i = 0; i < maxCount; i++) {
                _available.Add(new Lazy<FtpClient>(clientFactory, LazyThreadSafetyMode.ExecutionAndPublication));
            }
        }

        public FtpClientLease LeaseClient() {
            return new FtpClientLease(_available.Take(), Release);
        }

        private void Release(Lazy<FtpClient> lazy) {
            _available.Add(lazy);
        }

        public void Dispose() {
            _available.CompleteAdding();
            if (_available.Count != _available.BoundedCapacity)
                throw new InvalidOperationException("Some FtpClient instances were not returned to the pool before disposal.");

            var exceptions = new List<Exception>();
            foreach (var lazy in _available) {
                try {
                    if (lazy.IsValueCreated)
                        lazy.Value.Dispose();
                }
                catch (Exception ex) {
                    exceptions.Add(ex);
                }
            }
            if (exceptions.Count > 0)
                throw new AggregateException(exceptions);
        }
    }
}
