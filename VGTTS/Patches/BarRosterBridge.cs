using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Source.Galaxy.POI;
using Source.Galaxy.POI.Station;

namespace VGTTS.Patches;

/// <summary>Optional reflection boundary: older or absent API installations need no API assembly.</summary>
internal sealed class BarRosterBridge : IDisposable
{
    internal static BarRosterBridge? Current;
    private readonly PropertyInfo _bars;
    private readonly object _service;
    private readonly PropertyInfo _lifecycle;
    private readonly FinalizedPatronAdmission<BarPatron> _admission = new();
    private IDisposable? _subscription;
    private BarRosterBridge(PropertyInfo bars, object service, PropertyInfo lifecycle)
    { _bars = bars; _service = service; _lifecycle = lifecycle; }

    internal bool CanWarm(BarPatron patron)
    {
        if (!IsActive) return false;
        try
        {
            var lifecycle = _lifecycle.GetValue(null);
            var session = lifecycle?.GetType().GetProperty("CurrentSession")?.GetValue(lifecycle);
            var id = session?.GetType().GetProperty("Id")?.GetValue(session);
            var station = SpaceStation.current;
            return id is Guid sessionId && station?.bar != null
                && _admission.Allows(sessionId, station, station.bar.availablePatrons, patron);
        }
        catch { return false; }
    }
    internal bool IsActive
    {
        get { try { return _subscription != null && ReferenceEquals(_bars.GetValue(null), _service); } catch { return false; } }
    }

    internal static void Start()
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(value => value.GetName().Name == "VGModAPI.Abstractions");
            var property = assembly?.GetType("VGModAPI.ModApi")?.GetProperty("Bars", BindingFlags.Public | BindingFlags.Static);
            var service = property?.GetValue(null);
            var lifecycle = assembly?.GetType("VGModAPI.ModApi")?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
            var api = assembly?.GetType("VGModAPI.IBarApi");
            var snapshot = assembly?.GetType("VGModAPI.BarRosterFinalized");
            if (property == null || service == null || api == null || snapshot == null || lifecycle == null) return;
            var callbackType = typeof(Action<>).MakeGenericType(snapshot);
            var subscribe = api.GetMethod("Subscribe", new[] { typeof(string), callbackType });
            if (subscribe == null) return;
            var bridge = new BarRosterBridge(property, service, lifecycle);
            var callback = Delegate.CreateDelegate(callbackType, bridge,
                typeof(BarRosterBridge).GetMethod(nameof(Finalized), BindingFlags.NonPublic | BindingFlags.Instance)!);
            bridge._subscription = (IDisposable?)subscribe.Invoke(service, new object[] { Plugin.PluginGuid, callback });
            if (bridge._subscription == null) return;
            Current = bridge;
            Plugin.Log.LogInfo("Bar TTS observes API-finalized rosters.");
        }
        catch (Exception error) { Plugin.Log.LogWarning("Finalized bar observation unavailable: " + error.GetType().Name); }
    }

    private void Finalized(object snapshot)
    {
        if (!IsActive) return;
        var station = SpaceStation.current;
        if (station?.bar == null) return;
        var type = snapshot.GetType();
        if ((string?)type.GetProperty("StationId")?.GetValue(snapshot) != station.guid) return;
        if (type.GetProperty("Members")?.GetValue(snapshot) is not IEnumerable members) return;
        var after = station.bar.availablePatrons.ToArray();
        var projected = members.Cast<object>().ToArray();
        if (after.Length != projected.Length) return;
        for (int i = 0; i < after.Length; i++)
        {
            var row = projected[i]; var shape = row.GetType();
            if (!Equals(shape.GetProperty("Seat")?.GetValue(row), after[i].seat)
                || !Equals(shape.GetProperty("Seed")?.GetValue(row), after[i].seed)
                || !Equals(shape.GetProperty("NativeKind")?.GetValue(row), after[i].GetType().Name)) return;
        }
        if (type.GetProperty("SessionId")?.GetValue(snapshot) is not Guid sessionId || sessionId == Guid.Empty) return;
        _admission.Record(sessionId, station, after, after.Where((_, index) =>
            projected[index].GetType().GetProperty("OwnedId")?.GetValue(projected[index]) == null));
        for (int i = 0; i < after.Length; i++)
        {
            // Managed contacts own their narrative/voice behavior, not native Salesman dialogue.
            if (projected[i].GetType().GetProperty("OwnedId")?.GetValue(projected[i]) == null)
                BarPatronPatches.WarmFinalized(after[i]);
        }
        // Acquire replacement patrons' shared cache paths before retiring the old owners.
        BarRefreshPatches.ApplyFinalized(station.bar, after);
    }

    public void Dispose()
    {
        _subscription?.Dispose(); _subscription = null; _admission.Clear();
        if (ReferenceEquals(Current, this)) Current = null;
    }
}
