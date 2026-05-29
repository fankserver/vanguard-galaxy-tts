using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Source.Data;
using Source.Galaxy;
using Source.SpaceShip;
using VGTTS.Audio;
using VGTTS.Text;

namespace VGTTS.Patches;

/// <summary>
/// Pre-warms the rescue dialogue for distress-combat encounters at the
/// moment they're generated — during jump travel, before the player even
/// arrives at the POI.
///
/// Flow (Source.Simulation.TravelEvents.DistressCombat.CreateDynamicPOI):
///   1. Jump engine rolls a dynamic event, decides "distress call"
///   2. <c>CreateDynamicPOI</c> builds the Combat POI, spawns the
///      friendly ship (random commander firstName) + pirate guards
///   3. POI is returned; player eventually arrives and engages combat
///   4. Combat ends → DistressCombat.UpdateActive triggers the rescue
///      dialogue using the friend ship's commanderData.firstName
///
/// We postfix step 2: by then the friend ship is in the POI, so we can
/// grab its firstName and warm the 2 procedural dialogue lines
/// (captain's reply line is already covered by DefaultVoiceMap). This
/// fires exactly once per encounter — no per-frame overhead.
///
/// TODO: evict the warmed WAVs when the POI is removed
/// (<c>SystemMapData.RemovePointOfInterest</c>). Today the session/
/// cache dir is wiped on plugin load, which bounds growth to a single
/// launch. Tighter scoping would track POI → warmed paths via
/// ConditionalWeakTable the same way BarRefreshPatches does for bar
/// patrons, and call TtsController.DropCache when the POI exits.
/// Saves a few MB mid-session; not urgent while session wipe handles
/// the flooding concern.
/// </summary>
[HarmonyPatch(typeof(Source.Simulation.TravelEvents.DistressCombat))]
internal static class DistressCombatPatches
{
    private const string LineRescued =
        "Phew, that was a close call! Those pirates disabled our warp drive, they would've had us for lunch! Thanks for the help!";
    private const string LineReward =
        "Err, yes, of course. Here you go.";

    // Same procedural defaults as BarPatronPatches — kept out-of-sync-free by
    // duplicating the two constants; both should map to Kokoro SIDs no named
    // NPC uses so random rescue victims don't sound like a named character.
    private const string ProceduralMaleVoice   = "kokoro:12";  // am_echo
    private const string ProceduralFemaleVoice = "kokoro:9";   // af_sarah

    [HarmonyPostfix]
    [HarmonyPatch(nameof(Source.Simulation.TravelEvents.DistressCombat.CreateDynamicPOI))]
    private static void CreateDynamicPOI_Postfix(MapPointOfInterest __result)
    {
        if (__result == null) return;
        var controller = TtsController.Instance;
        if (controller == null) return;
        if (Plugin.Instance == null || !Plugin.Instance.CfgEnabled.Value || !Plugin.Instance.CfgDialogue.Value) return;

        string? friendName = null;
        bool friendIsMale = true;
        foreach (AbstractUnitData unit in __result.GetUnits())
        {
            if (unit.IsPlayerEnemy()) continue;
            if (unit is SpaceShipData ship && ship.commanderData != null)
            {
                friendName = ship.commanderData.firstName;
                friendIsMale = ship.commanderData.gender == Source.Personnel.Gender.Male;
                break;
            }
        }
        if (string.IsNullOrEmpty(friendName)) return;

        controller.RegisterVoice(friendName!,
            friendIsMale ? ProceduralMaleVoice : ProceduralFemaleVoice);

        Plugin.Log.LogDebug($"[distress-warm] Rescue POI spawned — pre-warming dialogue for '{friendName}' ({(friendIsMale ? "M" : "F")})");
        _ = Task.Run(async () =>
        {
            foreach (var line in new[] { LineRescued, LineReward })
            {
                try
                {
                    await controller.WarmCacheAsync(friendName!, TextNormalizer.ForTts(line), CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch { /* best-effort; StartDialogue hook will retry on-demand */ }
            }
        });
    }
}
