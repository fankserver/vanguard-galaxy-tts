using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VGTTS.Audio;

/// <summary>Retains shared cache paths until their last patron and in-flight warm have retired.</summary>
internal sealed class BarWarmRetention
{
    private sealed class Entry
    {
        internal int Owners, Running;
        internal readonly Action Evict;
        internal Entry(Action evict) { Evict = evict; }
    }
    internal sealed class Lease : IDisposable
    {
        private readonly BarWarmRetention _registry;
        internal readonly string Key;
        internal readonly Func<Task> Warm;
        internal bool Active = true;
        internal Lease(BarWarmRetention registry, string key, Func<Task> warm) { _registry = registry; Key = key; Warm = warm; }
        internal Task Run() => _registry.Run(this);
        public void Dispose() { _registry.Release(this); GC.SuppressFinalize(this); }
        // Patrons held only by a weak table can disappear on scene replacement without an
        // explicit roster diff. Release their cache ownership without retaining native objects.
        ~Lease() { try { _registry.Release(this); } catch { } }
    }
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal Lease Acquire(string key, Func<Task> warm, Action evict)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry)) _entries.Add(key, entry = new Entry(evict));
            entry.Owners++;
            return new Lease(this, key, warm);
        }
    }
    private async Task Run(Lease lease)
    {
        Entry entry;
        lock (_gate)
        {
            if (!lease.Active) return;
            entry = _entries[lease.Key]; entry.Running++;
        }
        try { await lease.Warm().ConfigureAwait(false); }
        finally
        {
            lock (_gate) { entry.Running--; Retire(lease.Key, entry); }
        }
    }
    private void Release(Lease lease)
    {
        lock (_gate)
        {
            if (!lease.Active) return;
            lease.Active = false;
            var entry = _entries[lease.Key]; entry.Owners--; Retire(lease.Key, entry);
        }
    }
    private void Retire(string key, Entry entry)
    {
        if (entry.Owners != 0 || entry.Running != 0) return;
        _entries.Remove(key);
        try { entry.Evict(); } catch { }
    }
}
