using System;
using System.Collections.Generic;
using iStoreOS.PluginHelper.Models;

namespace iStoreOS.PluginHelper.Services
{
    public sealed class MaintenanceOperationLogBuffer
    {
        private readonly object sync = new object();
        private Guid current;
        private readonly List<MaintenanceOperationLog> entries = new List<MaintenanceOperationLog>();
        public void Begin(Guid operationId)
        { if (operationId == Guid.Empty) throw new ArgumentException("Operation id required."); lock (sync) { current = operationId; entries.Clear(); } }
        public bool TryAppend(MaintenanceOperationLog entry)
        {
            if (entry == null) return false;
            lock (sync) { if (entry.OperationId != current) return false; if (entries.Count >= 1000) entries.RemoveAt(0); entries.Add(entry); return true; }
        }
        public IList<MaintenanceOperationLog> Snapshot()
        { lock (sync) return entries.ToArray(); }
    }
}
