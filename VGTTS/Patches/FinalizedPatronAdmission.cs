using System;
using System.Collections.Generic;
using System.Linq;

namespace VGTTS.Patches;

/// <summary>Allows delayed initialization only while the exact finalized vanilla roster is current.</summary>
internal sealed class FinalizedPatronAdmission<T> where T : class
{
    private Guid _session;
    private object? _station;
    private T[] _roster = Array.Empty<T>(), _vanilla = Array.Empty<T>();
    internal void Record(Guid session, object station, IEnumerable<T> roster, IEnumerable<T> vanilla)
    {
        _session = session; _station = station; _roster = roster.ToArray(); _vanilla = vanilla.ToArray();
    }
    internal bool Allows(Guid session, object station, IReadOnlyList<T> roster, T patron)
    {
        if (session == Guid.Empty || session != _session || !ReferenceEquals(station, _station)
            || roster.Count != _roster.Length || !_vanilla.Any(value => ReferenceEquals(value, patron))) return false;
        for (int i = 0; i < _roster.Length; i++) if (!ReferenceEquals(roster[i], _roster[i])) return false;
        return true;
    }
    internal void Clear() { _session = Guid.Empty; _station = null; _roster = _vanilla = Array.Empty<T>(); }
}
