using System;
using VGTTS.Patches;
using Xunit;

namespace VGTTS.Tests;

public sealed class FinalizedPatronAdmissionTests
{
    [Fact]
    public void FinalizationBeforeInitializationAllowsTheCurrentVanillaPatronLater()
    {
        var admission = new FinalizedPatronAdmission<object>();
        var session = Guid.NewGuid(); var station = new object(); var vanilla = new object(); var managed = new object();
        var roster = new[] { vanilla, managed };
        Assert.False(admission.Allows(session, station, roster, vanilla));
        admission.Record(session, station, roster, new[] { vanilla });
        // The admission survives the delay until native Initialize has populated dialogue data.
        Assert.True(admission.Allows(session, station, roster, vanilla));
        Assert.True(admission.Allows(session, station, roster, vanilla));
        Assert.False(admission.Allows(session, station, roster, managed));
    }

    [Fact]
    public void SupersededPatronsSessionsAndStationReferencesCannotWarm()
    {
        var admission = new FinalizedPatronAdmission<object>();
        var session = Guid.NewGuid(); var station = new object(); var old = new object(); var next = new object();
        admission.Record(session, station, new[] { old }, new[] { old });
        Assert.False(admission.Allows(Guid.NewGuid(), station, new[] { old }, old));
        Assert.False(admission.Allows(session, new object(), new[] { old }, old));
        Assert.False(admission.Allows(session, station, new[] { next }, old));
        admission.Record(session, station, new[] { next }, new[] { next });
        Assert.False(admission.Allows(session, station, new[] { old }, old));
        admission.Clear();
        Assert.False(admission.Allows(session, station, new[] { next }, next));
    }
}
