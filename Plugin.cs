using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace BenchStreamer;

[BepInPlugin("com.benchstreamer.em2026", "Bench Streamer", "1.1")]
public sealed class Plugin : BasePlugin
{
    internal static Plugin Instance { get; private set; }
    internal static ConfigEntry<int> NormalPrGain { get; private set; }
    internal static ConfigEntry<int> ViralPrGain { get; private set; }
    internal static ConfigEntry<float> ViralChancePercent { get; private set; }

    public override void Load()
    {
        Instance = this;
        NormalPrGain = Config.Bind("Gameplay", "PrPerSixMonthReview", 1, "PR gained by the streamer and club at a normal six-month review.");
        ViralPrGain = Config.Bind("Gameplay", "ViralPrPerSixMonthReview", 2, "Total PR gained by the streamer and club after a viral match broadcast.");
        ViralChancePercent = Config.Bind("Gameplay", "ViralChancePercent", 0.3f, "Chance of a viral story at a six-month PR review (0.1-0.5 percent).");

        var harmony = new Harmony("com.benchstreamer.em2026");
        PatchDayAdvanceHooks(harmony);
        PatchIfFound(harmony, "UserEventEngine", "ApplyMonthlyPrPass", postfix: nameof(Patches.ProcessMonthlyStreams));
        PatchIfFound(harmony, "PlayerOverview", "Init", postfix: nameof(Patches.OnPlayerOverviewInit));
        PatchIfFound(harmony, "PlayerOverview", "InitMainRoleDropdowm", postfix: nameof(Patches.AddStreamerOption));
        PatchIfFound(harmony, "PlayerOverview", "Update", postfix: nameof(Patches.EnforceStreamerPresentation));
        PatchIfFound(harmony, "PlayerOverview", "MainRoleChanged", prefix: nameof(Patches.HandleMainRoleChanged), postfix: nameof(Patches.RefreshStreamerPresentation));
        PatchBenchNotices(harmony);
        PatchIfFound(harmony, "TalkEventEngine", "FindBenchSittingCandidate", postfix: nameof(Patches.FilterBenchSittingCandidate));
        PatchIfFound(harmony, "EmailView", "Init", prefix: nameof(Patches.RepairStreamerEmailActions));

        Log.LogInfo("Bench Streamer loaded. Manual Streamer role selection, six-month PR reviews, and bench-notice suppression are active.");
    }

    private static void PatchIfFound(Harmony harmony, string typeName, string methodName, string prefix = null, string postfix = null)
    {
        Type type = FindLoadedType(typeName);
        MethodInfo method = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == methodName);
        if (method == null)
        {
            Instance.Log.LogWarning($"Could not find {typeName}.{methodName}; this feature may not work with this game version.");
            return;
        }

        MethodInfo prefixMethod = string.IsNullOrEmpty(prefix)
            ? null
            : typeof(Patches).GetMethod(prefix, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo postfixMethod = string.IsNullOrEmpty(postfix)
            ? null
            : typeof(Patches).GetMethod(postfix, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        try
        {
            harmony.Patch(method,
                prefix: prefixMethod == null ? null : new HarmonyMethod(prefixMethod),
                postfix: postfixMethod == null ? null : new HarmonyMethod(postfixMethod));
            Instance.Log.LogInfo($"Patched {typeName}.{methodName}.");
        }
        catch (Exception ex)
        {
            Instance.Log.LogError($"Could not patch {typeName}.{methodName}; other mod hooks will continue loading. {ex.Message}");
        }
    }

    private static void PatchBenchNotices(Harmony harmony)
    {
        MethodInfo prefix = typeof(Patches).GetMethod(nameof(Patches.AllowBenchNotice), BindingFlags.Static | BindingFlags.Public);
        string[] targets =
        {
            "UserEventEngine.CheckBenchSittingNotifications",
            "UserEventEngine.CheckBenchSittingTalkDialogs",
            "TransferTriggerEngine.CheckBenchSitting"
        };
        int patched = 0;
        foreach (string targetName in targets)
        {
            string[] parts = targetName.Split('.');
            Type type = FindLoadedType(parts[0]);
            MethodInfo target = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == parts[1]);
            if (target == null)
            {
                Instance.Log.LogWarning($"Could not find {targetName}; reserve-player complaints may still appear.");
                continue;
            }
            try
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                Instance.Log.LogInfo($"Patched reserve bench-notice hook {targetName}.");
                patched++;
            }
            catch (Exception ex)
            {
                Instance.Log.LogWarning($"Could not patch {targetName}; reserve-player complaints may still appear. {ex.Message}");
            }
        }
        Instance.Log.LogInfo($"Reserve bench-notice hooks installed: {patched}/{targets.Length}.");
    }

    private static void PatchDayAdvanceHooks(Harmony harmony)
    {
        string[] names = { "NextDayEngineSync", "NextDayEngine", "NextDayDate" };
        MethodInfo postfix = typeof(Patches).GetMethod(nameof(Patches.RememberManagedTeam), BindingFlags.Static | BindingFlags.Public);
        int patched = 0;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string assemblyName = assembly.GetName().Name;
            if (assemblyName != "EsportsManager" && assemblyName != "Assembly-CSharp")
                continue;
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(type => type != null).ToArray(); }
            catch { continue; }

            foreach (Type type in types)
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                if (!names.Contains(method.Name, StringComparer.Ordinal)
                    || typeof(System.Collections.IEnumerator).IsAssignableFrom(method.ReturnType))
                    continue;
                try
                {
                    harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                    Instance.Log.LogInfo($"Patched day-advance hook {type.Name}.{method.Name}.");
                    patched++;
                }
                catch (Exception ex)
                {
                    Instance.Log.LogDebug($"Skipped day hook {type.Name}.{method.Name}: {ex.Message}");
                }
            }
        }
        if (patched == 0)
            Instance.Log.LogWarning("No synchronous day-advance hook was found; start-streaming emails cannot be triggered.");
    }

    internal static Type FindLoadedType(string fullName)
    {
        Type type;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(fullName, false);
            if (type != null)
                return type;
        }

        // Some EM26 interop builds place game types in a namespace, while
        // Harmony still exposes methods by their short type names.
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string assemblyName = assembly.GetName().Name;
            if (assemblyName != "EsportsManager" && assemblyName != "Assembly-CSharp")
                continue;
            try
            {
                type = assembly.GetTypes().FirstOrDefault(candidate => candidate != null && candidate.Name == fullName);
                if (type != null)
                    return type;
            }
            catch (ReflectionTypeLoadException ex)
            {
                type = ex.Types.FirstOrDefault(candidate => candidate != null && candidate.Name == fullName);
                if (type != null)
                    return type;
            }
            catch
            {
                // Keep searching other game assemblies.
            }
        }
        return null;
    }
}

internal static class Patches
{
    private static string _managedTeamId;
    private static readonly HashSet<string> ActiveStreamerIds = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (object Player, ConfigEntry<bool> Pending)> AnnouncementEntries = new(StringComparer.Ordinal);
    private static readonly System.Random Random = new();

    public static void OnPlayerOverviewInit(object __instance)
    {
        Plugin.Instance?.Log.LogInfo("PlayerOverview.Init ran; adding the Streamer role option.");
        AddStreamerOption(__instance);
    }

    public static void EnforceStreamerPresentation(object __instance)
    {
        try
        {
            object player = ReadMember(__instance, "_player");
            if (player == null || !IsStreamer(player))
                return;
            int index = FindStreamerOptionIndex(__instance);
            TMPro.TMP_Dropdown dropdown = ReadMember(__instance, "_mainRoleDropdown") as TMPro.TMP_Dropdown;
            if (index >= 0 && dropdown != null && dropdown.value != index)
            {
                dropdown.SetValueWithoutNotify(index);
                dropdown.RefreshShownValue();
            }
            SetVisibleRoleLabel(__instance, player, true);
        }
        catch (Exception ex)
        {
            Plugin.Instance?.Log.LogDebug($"Could not enforce Streamer presentation: {ex.Message}");
        }
    }

    public static void RememberManagedTeam(object __instance)
    {
        try
        {
            object managedTeam = GetCurrentManagedTeam();
            _managedTeamId = ReadString(managedTeam, "Id")
                ?? NormalizeTeamId(ReadString(__instance, "teamId"));
            if (!string.IsNullOrWhiteSpace(_managedTeamId))
                RefreshActiveStreamer(_managedTeamId);
            Plugin.Instance?.Log.LogInfo($"A day-advance hook ran ({__instance?.GetType().Name}).");
            if (TryGetCurrentGameDate(out DateTime gameDate))
                ProcessPendingStreamerAnnouncements(gameDate);
            else
                Plugin.Instance?.Log.LogWarning("Could not read the in-game date for pending Streamer announcements; they will retry next day.");
        }
        catch (Exception ex)
        {
            Plugin.Instance?.Log.LogDebug($"Could not read the managed club ID: {ex.Message}");
        }
    }

    public static void ProcessMonthlyStreams(object[] __args)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_managedTeamId) || __args == null || __args.Length == 0)
                return;

            if (!TryReadGameDate(__args[0], out DateTime gameDate))
            {
                Plugin.Instance.Log.LogWarning("Could not read the current in-game date; this month's streams were skipped.");
                return;
            }

            ProcessClubStreams(_managedTeamId, gameDate);
        }
        catch (Exception ex)
        {
            Plugin.Instance?.Log.LogError($"Monthly PR review processing failed safely: {ex}");
        }
    }

    private static bool TryGetCurrentGameDate(out DateTime gameDate)
    {
        gameDate = default;
        Type values = Plugin.FindLoadedType("GlobalValues");
        PropertyInfo currentDate = values?.GetProperty("CurrentDate", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        object dateValue = currentDate?.GetValue(null);
        return TryReadGameDate(dateValue, out gameDate);
    }

    private static void ProcessPendingStreamerAnnouncements(DateTime gameDate)
    {
        foreach (object player in ReadPendingAnnouncementPlayers())
        {
            string playerId = ReadString(player, "Id") ?? ReadString(player, "Nick") ?? "unknown";
            Plugin.Instance.Log.LogInfo($"Found pending start-streaming email for player '{playerId}'.");
            if (player == null)
            {
                Plugin.Instance.Log.LogWarning($"Could not resolve player '{playerId}' for the pending start-streaming email; it will retry next game day.");
                continue;
            }
            if (!IsStreamer(player))
            {
                Plugin.Instance.Log.LogWarning($"Player '{playerId}' is not marked as Streamer in config; keeping the email pending.");
                continue;
            }

            object team = ResolveTeamForPlayer(player);
            if (team == null)
            {
                Plugin.Instance.Log.LogWarning($"Could not resolve {ReadString(player, "Nick")}'s club for the start-streaming email; it will retry next game day.");
                continue;
            }

            if (!SendStartedStreamingEmail(player, team, gameDate))
            {
                Plugin.Instance.Log.LogWarning($"Could not create the start-streaming email for {ReadString(player, "Nick")}; it will retry next game day.");
                continue;
            }

            GetStreamerAnnouncementEntry(player).Value = false;
            Plugin.Instance.Config.Save();
            Plugin.Instance.Log.LogInfo($"Sent the start-streaming email for {ReadString(player, "Nick")} on {gameDate:yyyy-MM-dd}.");
        }
    }

    private static IEnumerable<object> ReadPendingAnnouncementPlayers()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in AnnouncementEntries.Values)
        {
            if (item.Pending.Value && item.Player != null)
            {
                string id = ReadString(item.Player, "Id") ?? ReadString(item.Player, "Nick");
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                    yield return item.Player;
            }
        }

        foreach (ConfigDefinition definition in Plugin.Instance.Config.Keys)
        {
            if (!string.Equals(definition.Section, "StreamerAnnouncements", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Plugin.Instance.Config.TryGetEntry<bool>(definition, out ConfigEntry<bool> entry) || !entry.Value)
                continue;
            object player = GetEntity("DataPlayers", definition.Key);
            if (player == null)
                player = GetEntity("DataPlayers", definition.Key.Contains('_') ? definition.Key[(definition.Key.LastIndexOf('_') + 1)..] : definition.Key);
            if (player != null)
            {
                GetStreamerAnnouncementEntry(player);
                string id = ReadString(player, "Id") ?? ReadString(player, "Nick");
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                    yield return player;
            }
        }
    }

    public static void AddStreamerOption(object __instance)
    {
        try
        {
            object player = ReadMember(__instance, "_player");
            if (player == null)
            {
                Plugin.Instance?.Log.LogWarning("Player profile has no player data; Streamer role was not added.");
                return;
            }
            TMPro.TMP_Dropdown dropdown = ReadMember(__instance, "_mainRoleDropdown") as TMPro.TMP_Dropdown;
            if (dropdown == null)
            {
                Plugin.Instance.Log.LogWarning("Player profile has no main-role dropdown; Streamer role was not added.");
                return;
            }

            var options = dropdown.options;
            if (options == null)
            {
                Plugin.Instance.Log.LogWarning("Main-role dropdown has no options list; Streamer role was not added.");
                return;
            }

            string label = GetStreamerLabel();
            int optionIndex = 0;
            int streamerIndex = -1;
            foreach (object option in ReadObjectsList(options))
            {
                if (string.Equals(ReadString(option, "text"), label, StringComparison.OrdinalIgnoreCase))
                    streamerIndex = optionIndex;
                optionIndex++;
            }
            if (streamerIndex < 0)
            {
                options.Add(new TMPro.TMP_Dropdown.OptionData(label));
                streamerIndex = optionIndex;
            }

            if (IsStreamer(player))
            {
                GetStreamerAnnouncementEntry(player);
                MovePlayerToBench(player);
                dropdown.SetValueWithoutNotify(streamerIndex);
                dropdown.RefreshShownValue();
                SetVisibleRoleLabel(__instance, player, true);
            }

            Plugin.Instance.Log.LogInfo($"Streamer role option ready for {ReadString(player, "Nick")} at dropdown index {streamerIndex}; options={optionIndex + (streamerIndex == optionIndex ? 1 : 0)}.");
        }
        catch (Exception ex)
        {
            Plugin.Instance?.Log.LogError($"Could not add the Streamer role option: {ex}");
        }
    }

    private static int FindStreamerOptionIndex(object overview)
    {
        TMPro.TMP_Dropdown dropdown = ReadMember(overview, "_mainRoleDropdown") as TMPro.TMP_Dropdown;
        if (dropdown?.options == null)
            return -1;
        string label = GetStreamerLabel();
        int index = 0;
        foreach (object option in ReadObjectsList(dropdown.options))
        {
            if (string.Equals(ReadString(option, "text"), label, StringComparison.OrdinalIgnoreCase))
                return index;
            index++;
        }
        return -1;
    }

    public static void RefreshStreamerPresentation(object __instance)
    {
        try
        {
            object player = ReadMember(__instance, "_player");
            if (player == null)
                return;
            bool active = IsStreamer(player);
            SetVisibleRoleLabel(__instance, player, active);
        }
        catch (Exception ex)
        {
            Plugin.Instance?.Log.LogError($"Could not refresh Streamer role display: {ex.Message}");
        }
    }

    public static bool HandleMainRoleChanged(object __instance, int __0)
    {
        try
        {
            object player = ReadMember(__instance, "_player");
            if (player == null)
                return true;

            int streamerIndex = FindStreamerOptionIndex(__instance);
            if (streamerIndex >= 0 && __0 == streamerIndex)
            {
                SetStreamer(player, true);
                bool moved = MovePlayerToBench(player);
                if (!moved)
                    Plugin.Instance.Log.LogWarning($"{ReadString(player, "Nick")} was saved as Streamer, but the game refused the optional squad move.");
                string selectedTeamId = ReadString(player, "TeamId");
                if (!string.IsNullOrWhiteSpace(selectedTeamId))
                    RefreshActiveStreamer(selectedTeamId);
                Plugin.Instance.Log.LogInfo($"{ReadString(player, "Nick")} was assigned Streamer; next-day announcement queued.");
                return false;
            }

            if (IsStreamer(player))
            {
                SetStreamer(player, false);
                string teamId = ReadString(player, "TeamId");
                if (!string.IsNullOrWhiteSpace(teamId))
                    RefreshActiveStreamer(teamId);
                Plugin.Instance.Log.LogInfo($"{ReadString(player, "Nick")} was removed from the Streamer role.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Instance?.Log.LogError($"Could not save the selected player role: {ex.Message}");
        }

        return true;
    }

    private static bool MovePlayerToBench(object player)
    {
        string teamId = ReadString(player, "TeamId");
        string playerId = ReadString(player, "Id");
        string playerNick = ReadString(player, "Nick");
        object team = ResolveTeamForPlayer(player);
        object mainSquad = ReadMember(team, "MainSquad");
        if (team == null || mainSquad == null)
        {
            Plugin.Instance.Log.LogWarning($"Could not find team/MainSquad to move {playerNick} to the reserve squad (teamId='{teamId}', playerId='{playerId}', teamFound={team != null}, mainSquadFound={mainSquad != null}).");
            return false;
        }

        bool listedByNick = !string.IsNullOrWhiteSpace(playerNick) && ContainsString(mainSquad, playerNick);
        bool listedById = !string.IsNullOrWhiteSpace(playerId) && ContainsString(mainSquad, playerId);
        if (!listedByNick && !listedById)
            return true;

        MethodInfo remove = team.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(method => method.Name == "RemovePlayerFromMainSquad"
                && method.GetParameters().Length == 1
                && method.GetParameters()[0].ParameterType == typeof(string));
        if (remove == null || string.IsNullOrWhiteSpace(playerId))
        {
            Plugin.Instance.Log.LogWarning($"Could not find RemovePlayerFromMainSquad(string) for {playerNick}.");
            return false;
        }

        remove.Invoke(team, new object[] { playerId });
        mainSquad = ReadMember(team, "MainSquad");
        bool stillListed = mainSquad != null
            && ((!string.IsNullOrWhiteSpace(playerNick) && ContainsString(mainSquad, playerNick))
                || (!string.IsNullOrWhiteSpace(playerId) && ContainsString(mainSquad, playerId)));
        if (stillListed)
            Plugin.Instance.Log.LogWarning($"Game did not remove {playerNick} from the Main Squad.");
        return !stillListed;
    }

    public static bool AllowBenchNotice(object[] __args)
    {
        try
        {
            if (__args == null)
                return true;

            foreach (object arg in __args)
            {
                if (arg == null)
                    continue;
                string playerId = ReadString(arg, "Id");
                if (!string.IsNullOrEmpty(playerId) && ActiveStreamerIds.Contains(playerId))
                    return false;

            }
        }
        catch (Exception ex)
        {
            Plugin.Instance?.Log.LogDebug($"Bench notice was allowed after a safe check failure: {ex.Message}");
        }

        return true;
    }

    public static void FilterBenchSittingCandidate(ref DataPlayer __result)
    {
        try
        {
            if (__result == null || ReadBool(__result, "Retired") || !IsStreamer(__result))
                return;

            string playerNick = ReadString(__result, "Nick");
            bool wasOnBench = IsOnBench(__result);
            bool movedToBench = wasOnBench || MovePlayerToBench(__result);
            Plugin.Instance?.Log.LogInfo($"Suppressed bench-sitting candidate for active streamer '{playerNick}' ({ReadString(__result, "Id")}); alreadyOnBench={wasOnBench}, movedOrConfirmedOnBench={movedToBench}.");
            __result = null;
        }
        catch (Exception ex)
        {
            Plugin.Instance?.Log.LogWarning($"Could not filter a bench-sitting candidate safely: {ex.Message}");
        }
    }

    private static void ProcessClubStreams(string teamId, DateTime gameDate)
    {
        ActiveStreamerIds.Clear();
        object team = ResolveTeamByKey(teamId);
        teamId = ReadString(team, "Id") ?? teamId;
        if (team == null || ReadBool(team, "IsDisbanded"))
            return;

        object mainSquad = ReadMember(team, "MainSquad");
        object fullSquad = ReadMember(team, "FullSquad");
        if (mainSquad == null || fullSquad == null)
            return;

        var streamers = new List<(string Id, object Player, DateTime BenchedSince)>();
        foreach (string playerId in ReadStringList(fullSquad))
        {
            if (ContainsString(mainSquad, playerId))
                continue;

            object player = GetEntity("DataPlayers", playerId);
            if (player == null || !IsStreamer(player) || ReadBool(player, "Retired") || ReadString(player, "TeamId") != teamId)
                continue;

            if (!TryReadGameDate(ReadMember(player, "BenchedSinceDate"), out DateTime benchedSince))
                continue;
            streamers.Add((playerId, player, benchedSince));
        }

        foreach (var streamer in streamers)
        {
            ActiveStreamerIds.Add(streamer.Id);
            if ((gameDate.Date - streamer.BenchedSince.Date).TotalDays < 14)
                continue;

            ConfigEntry<int> progress = GetProgressEntry(teamId, streamer.Id, streamer.BenchedSince);
            progress.Value = Math.Clamp(progress.Value + 1, 0, 6);
            Plugin.Instance.Config.Save();
            if (progress.Value < 6)
                continue;

            progress.Value = 0;
            Plugin.Instance.Config.Save();

            float viralChancePercent = Math.Clamp(Plugin.ViralChancePercent.Value, 0.1f, 0.5f);
            bool viral = Random.NextDouble() * 100.0 < viralChancePercent;
            int gain = viral
                ? Math.Max(Plugin.NormalPrGain.Value, Plugin.ViralPrGain.Value)
                : Math.Max(0, Plugin.NormalPrGain.Value);

            ChangePr(streamer.Player, gain);
            ChangePr(team, gain);

            if (viral)
                SendViralEmail(streamer.Player, team, gameDate, gain);

            Plugin.Instance.Log.LogInfo($"{(viral ? "Viral" : "Normal")} streamer PR review: {ReadString(streamer.Player, "Nick")} (+{gain} PR to player and club).");
        }
    }

    private static void RefreshActiveStreamer(string teamId)
    {
        ActiveStreamerIds.Clear();
        object team = ResolveTeamByKey(teamId);
        teamId = ReadString(team, "Id") ?? teamId;
        object mainSquad = ReadMember(team, "MainSquad");
        object fullSquad = ReadMember(team, "FullSquad");
        if (mainSquad == null || fullSquad == null)
            return;

        foreach (string playerId in ReadStringList(fullSquad))
        {
            if (ContainsString(mainSquad, playerId))
                continue;

            object player = GetEntity("DataPlayers", playerId);
            if (player == null || ReadBool(player, "Retired") || ReadString(player, "TeamId") != teamId)
                continue;

            if (IsStreamer(player))
                ActiveStreamerIds.Add(playerId);
        }
    }

    private static bool IsStreamer(object player) => GetStreamerEntry(player).Value;

    private static ConfigEntry<bool> GetStreamerEntry(object player)
    {
        string teamId = ReadString(player, "TeamId") ?? "unknown_team";
        string playerId = ReadString(player, "Id") ?? "unknown_player";
        string rawKey = $"{teamId}_{playerId}";
        string key = new string(rawKey.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return Plugin.Instance.Config.Bind("StreamerPlayers", key, false, "Players manually assigned the Streamer role in the player overview.");
    }

    private static void SetStreamer(object player, bool selected)
    {
        GetStreamerEntry(player).Value = selected;
        GetStreamerAnnouncementEntry(player).Value = selected;
        Plugin.Instance.Config.Save();
        string nick = ReadString(player, "Nick");
        string teamId = ReadString(player, "TeamId");
        string playerId = ReadString(player, "Id");
        Plugin.Instance.Log.LogInfo($"Saved Streamer={selected} for {nick} (teamId='{teamId}', playerId='{playerId}').");
    }

    private static ConfigEntry<bool> GetStreamerAnnouncementEntry(object player)
    {
        string playerId = ReadString(player, "Id") ?? "unknown_player";
        string key = new string(playerId.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        ConfigEntry<bool> entry = Plugin.Instance.Config.Bind("StreamerAnnouncements", key, false, "Send one in-game email the day after assigning Streamer.");
        AnnouncementEntries[key] = (player, entry);
        return entry;
    }

    private static object ResolveTeamForPlayer(object player)
    {
        foreach (string member in new[] { "Team", "DataTeam", "Club" })
        {
            object team = ReadMember(player, member);
            if (team != null && (ReadString(team, "Id") != null || ReadString(team, "Nick") != null))
                return team;
        }

        string teamId = ReadString(player, "TeamId");
        if (string.IsNullOrWhiteSpace(teamId))
            return null;

        object managedTeam = GetCurrentManagedTeam();
        if (managedTeam != null)
        {
            string managedId = ReadString(managedTeam, "Id");
            string managedNick = ReadString(managedTeam, "Nick");
            object fullSquad = ReadMember(managedTeam, "FullSquad");
            string playerId = ReadString(player, "Id");
            bool belongsToManagedTeam = string.Equals(teamId, managedId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(teamId, managedNick, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(playerId) && ContainsString(fullSquad, playerId));
            if (belongsToManagedTeam)
            {
                Plugin.Instance?.Log.LogDebug($"Resolved {ReadString(player, "Nick")}'s team '{teamId}' through GlobalValues.DataTeam ({managedId}).");
                return managedTeam;
            }
        }

        object resolvedTeam = GetEntity("DataTeams", teamId);
        if (resolvedTeam != null)
            return resolvedTeam;
        return InvokeTeamNameResolver(teamId);
    }

    private static object GetCurrentManagedTeam()
    {
        Type globalValues = Plugin.FindLoadedType("GlobalValues");
        return ReadStaticMember(globalValues, "DataTeam");
    }

    private static object ResolveTeamByKey(string teamId)
    {
        if (string.IsNullOrWhiteSpace(teamId))
            return GetCurrentManagedTeam();
        object managedTeam = GetCurrentManagedTeam();
        if (managedTeam != null && (string.Equals(teamId, ReadString(managedTeam, "Id"), StringComparison.OrdinalIgnoreCase)
            || string.Equals(teamId, ReadString(managedTeam, "Nick"), StringComparison.OrdinalIgnoreCase)))
            return managedTeam;
        return GetEntity("DataTeams", teamId) ?? InvokeTeamNameResolver(teamId);
    }

    private static string NormalizeTeamId(string teamId)
    {
        object team = ResolveTeamByKey(teamId);
        return ReadString(team, "Id") ?? teamId;
    }

    private static object InvokeTeamNameResolver(string teamName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            string assemblyName = assembly.GetName().Name;
            if (assemblyName != "EsportsManager" && assemblyName != "Assembly-CSharp")
                continue;
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(type => type != null).ToArray(); }
            catch { continue; }
            foreach (Type type in types)
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (!string.Equals(method.Name, "GetTeamByName", StringComparison.Ordinal)
                    || method.GetParameters().Length != 1
                    || method.GetParameters()[0].ParameterType != typeof(string))
                    continue;
                try
                {
                    object team = method.Invoke(null, new object[] { teamName });
                    if (team != null && string.Equals(ReadString(team, "Nick"), teamName, StringComparison.OrdinalIgnoreCase))
                        return team;
                }
                catch { }
            }
        }
        return null;
    }

    private static bool IsOnBench(object player)
    {
        string playerId = ReadString(player, "Id");
        string playerNick = ReadString(player, "Nick");
        string teamId = ReadString(player, "TeamId");
        object team = ResolveTeamForPlayer(player);
        object mainSquad = ReadMember(team, "MainSquad");
        if (mainSquad == null)
        {
            Plugin.Instance?.Log.LogWarning($"Reserve check failed for {playerNick}: no MainSquad list (teamId='{teamId ?? "null"}').");
            return false;
        }
        bool isStarter = (!string.IsNullOrWhiteSpace(playerId) && ContainsString(mainSquad, playerId))
            || (!string.IsNullOrWhiteSpace(playerNick) && ContainsString(mainSquad, playerNick));
        return !isStarter;
    }

    private static string GetStreamerLabel()
    {
        return "Streamer";
    }

    private static void SetVisibleRoleLabel(object overview, object player, bool active)
    {
        string normalRole = ReadString(player, "MainRole");
        string displayedRole = active ? GetStreamerLabel() : normalRole;
        if (string.IsNullOrWhiteSpace(displayedRole))
            return;

        TMPro.TMP_Text roleText = ReadMember(overview, "_roleTxt") as TMPro.TMP_Text;
        if (roleText != null)
        {
            roleText.text = displayedRole;
            return;
        }

        UnityEngine.Component component = overview as UnityEngine.Component;
        if (component == null)
            return;

        foreach (TMPro.TMP_Text text in component.GetComponentsInChildren<TMPro.TMP_Text>(true))
        {
            if (text == null)
                continue;
            string current = text.text?.Trim();
            if (active && (string.Equals(current, "Rifler", StringComparison.OrdinalIgnoreCase)
                || string.Equals(current, "AWPer", StringComparison.OrdinalIgnoreCase)))
                text.text = displayedRole;
            else if (!active && string.Equals(current, GetStreamerLabel(), StringComparison.OrdinalIgnoreCase))
                text.text = displayedRole;
        }
    }

    private static ConfigEntry<int> GetProgressEntry(string teamId, string playerId, DateTime benchedSince)
    {
        string rawKey = $"{teamId}_{playerId}_{benchedSince:yyyyMMdd}";
        string key = new string(rawKey.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return Plugin.Instance.Config.Bind("StreamProgress", key, 0, "Saved in-game months toward this reserve player's next six-month PR review.");
    }

    private static void ChangePr(object entity, int amount)
    {
        if (amount <= 0)
            return;
        PropertyInfo property = entity.GetType().GetProperty("PrRating", BindingFlags.Public | BindingFlags.Instance)
            ?? entity.GetType().GetProperty("PRRating", BindingFlags.Public | BindingFlags.Instance);
        if (property == null || !property.CanRead || !property.CanWrite)
            return;
        int current = Convert.ToInt32(property.GetValue(entity), CultureInfo.InvariantCulture);
        property.SetValue(entity, current + amount);
    }

    private static void SendViralEmail(object player, object team, DateTime gameDate, int gain)
    {
        QueueEmail(player, team, gameDate, GetMailCopy(ReadLocaleCode()), gain);
    }

    private static bool SendStartedStreamingEmail(object player, object team, DateTime gameDate)
    {
        return QueueEmail(player, team, gameDate, GetStartedMailCopy(ReadLocaleCode()), 0);
    }

    public static void RepairStreamerEmailActions(object[] __args)
    {
        try
        {
            if (__args == null)
                return;
            object email = __args.FirstOrDefault(arg => arg != null && arg.GetType().Name == "DataEmail");
            if (email == null || ReadMember(email, "Content") != null || !IsBenchStreamerEmailSubject(ReadString(email, "Subject")))
                return;

            if (AttachDefaultEmailActions(email))
                Plugin.Instance?.Log.LogInfo($"Added the standard Okay action to existing Bench Streamer email '{ReadString(email, "Subject")}'.");
            else
                Plugin.Instance?.Log.LogWarning($"Could not add an Okay action to existing Bench Streamer email '{ReadString(email, "Subject")}'.");
        }
        catch (Exception ex)
        {
            Plugin.Instance?.Log.LogWarning($"Could not repair the Bench Streamer email action: {ex.Message}");
        }
    }

    private static bool IsBenchStreamerEmailSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return false;
        string[] locales = { "en", "tr", "es", "fr", "de", "ge", "it", "ja", "ko", "nl", "pl", "pt", "pr", "ru", "sv", "uk", "ua", "zh", "zn", "hr", "hu", "kk", "ro", "sk", "sl" };
        return locales.Any(locale => string.Equals(subject, GetStartedMailCopy(locale).Subject, StringComparison.Ordinal)
            || string.Equals(subject, GetMailCopy(locale).Subject, StringComparison.Ordinal));
    }

    private static bool QueueEmail(object player, object team, DateTime gameDate, MailCopy copy, int gain)
    {
        Type factory = Plugin.FindLoadedType("EmailAuthorFactory");
        Type emailType = Plugin.FindLoadedType("DataEmail");
        Type emails = Plugin.FindLoadedType("DataEmails");
        if (factory == null || emailType == null || emails == null)
            return false;

        MethodInfo createAuthor = factory.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(team));
        object author = createAuthor?.Invoke(null, new[] { team });
        if (author == null)
            return false;

        Type contentType = Plugin.FindLoadedType("AcknowledgementContent");
        ConstructorInfo contentConstructor = contentType?.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 0);
        object content = contentConstructor?.Invoke(Array.Empty<object>());
        if (content == null)
            return false;

        ConstructorInfo constructor = emailType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 3 && c.GetParameters()[0].ParameterType.IsInstanceOfType(author));
        object email = constructor?.Invoke(new[] { author, (object)true, content });
        if (email == null)
            return false;

        if (ReadMember(content, "Actions") == null && !InitializeAcknowledgementContent(content, email))
            return false;

        string playerName = ReadString(player, "Nick");
        string teamName = ReadString(team, "Nick");
        SetMember(email, "Subject", Format(copy.Subject, playerName, teamName, gain));
        SetMember(email, "Greetings", copy.Greeting);
        SetMember(email, "BodyBeforeContent", Format(copy.Body, playerName, teamName, gain));
        SetMember(email, "BodyAfterContent", string.Empty);
        SetMember(email, "Closing", copy.Closing);
        SetMember(email, "Date", CreateGameDateLike(email, gameDate));
        SetMember(email, "IsIncoming", true);
        SetMember(email, "IsActive", true);

        MethodInfo add = emails.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(email));
        if (add == null)
            return false;
        add.Invoke(null, new[] { email });
        return true;
    }

    private static bool AttachDefaultEmailActions(object email, object content = null)
    {
        Type contentType = Plugin.FindLoadedType("AcknowledgementContent");
        if (email == null || contentType == null)
            return false;

        content ??= ReadMember(email, "Content");
        if (content == null)
        {
            ConstructorInfo contentConstructor = contentType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Length == 0);
            content = contentConstructor?.Invoke(Array.Empty<object>());
        }
        if (content == null)
            return false;

        SetMember(email, "Content", content);
        SetMember(content, "Email", email);
        return InitializeAcknowledgementContent(content, email);
    }

    private static bool InitializeAcknowledgementContent(object content, object email)
    {
        if (content == null || email == null)
            return false;
        MethodInfo initialize = content.GetType().GetMethod("Initialize", BindingFlags.Public | BindingFlags.Instance,
            null, new[] { email.GetType() }, null);
        if (initialize == null)
            return false;
        initialize.Invoke(content, new[] { email });
        return ReadMember(content, "Actions") != null;
    }

    private static MailCopy GetStartedMailCopy(string locale)
    {
        string language = (locale ?? "en").Split('-', '_')[0].ToLowerInvariant();
        return language switch
        {
            "tr" => new MailCopy("Yayınlar başladı!", "Merhaba,", "{PLAYER}, {TEAM} için tam zamanlı yayıncı ve maç sunucusu olarak göreve başladı.", "Kulüp Yönetimi"),
            "es" => new MailCopy("¡Comienzan las retransmisiones!", "Hola,", "{PLAYER} ha empezado a retransmitir y presentar los partidos de {TEAM} a tiempo completo.", "La dirección del club"),
            "fr" => new MailCopy("Les streams commencent !", "Bonjour,", "{PLAYER} commence à streamer et à présenter les matchs de {TEAM} à plein temps.", "La direction du club"),
            "de" or "ge" => new MailCopy("Die Streams beginnen!", "Hallo,", "{PLAYER} beginnt ab sofort als Vollzeit-Streamer und Match-Moderator für {TEAM}.", "Die Vereinsleitung"),
            "it" => new MailCopy("Le dirette hanno inizio!", "Ciao,", "{PLAYER} ha iniziato a trasmettere e presentare le partite di {TEAM} a tempo pieno.", "La dirigenza del club"),
            "ja" => new MailCopy("配信が始まりました！", "こんにちは。", "{PLAYER}が{TEAM}の専属配信者兼試合ホストとして活動を始めました。", "クラブ運営部"),
            "ko" => new MailCopy("방송이 시작됩니다!", "안녕하세요,", "{PLAYER} 선수가 {TEAM}의 전담 스트리머 겸 경기 진행자로 활동을 시작했습니다.", "구단 운영진"),
            "nl" => new MailCopy("De streams gaan van start!", "Hallo,", "{PLAYER} is begonnen als fulltime streamer en wedstrijdpresentator voor {TEAM}.", "Het clubbestuur"),
            "pl" => new MailCopy("Transmisje ruszają!", "Witaj,", "{PLAYER} rozpoczyna pracę jako pełnoetatowy streamer i prowadzący mecze drużyny {TEAM}.", "Zarząd klubu"),
            "pt" or "pr" => new MailCopy("As transmissões começaram!", "Olá,", "{PLAYER} começou a transmitir e apresentar os jogos de {TEAM} a tempo inteiro.", "A direção do clube"),
            "ru" => new MailCopy("Трансляции начинаются!", "Здравствуйте!", "{PLAYER} начинает работать постоянным стримером и ведущим матчей команды {TEAM}.", "Руководство клуба"),
            "sv" => new MailCopy("Sändningarna börjar!", "Hej,", "{PLAYER} har börjat som heltidsstreamer och matchvärd för {TEAM}.", "Klubbledningen"),
            "uk" or "ua" => new MailCopy("Трансляції розпочалися!", "Вітаємо!", "{PLAYER} починає працювати штатним стрімером і ведучим матчів команди {TEAM}.", "Керівництво клубу"),
            "zh" or "zn" => new MailCopy("直播开始了！", "您好：", "{PLAYER}已开始担任{TEAM}的全职主播和比赛主持人。", "俱乐部管理层"),
            _ => new MailCopy("The streams are starting!", "Hello,", "{PLAYER} has started streaming and hosting matches full time for {TEAM}.", "Club Management")
        };
    }

    private static object CreateGameDateLike(object email, DateTime date)
    {
        PropertyInfo property = email.GetType().GetProperty("Date");
        Type dateType = property?.PropertyType;
        if (dateType == null)
            return null;

        ConstructorInfo ctor = dateType.GetConstructor(new[] { typeof(int), typeof(int), typeof(int) });
        if (ctor != null)
            return ctor.Invoke(new object[] { date.Year, date.Month, date.Day });
        return null;
    }

    private static MailCopy GetMailCopy(string locale)
    {
        string language = (locale ?? "en").Split('-', '_')[0].ToLowerInvariant();
        return language switch
        {
            "tr" => new MailCopy("Takımın maç yayını viral oldu!", "Merhaba,", "{PLAYER} oyuncusunun {TEAM} maçı için yaptığı sunum viral oldu. Oyuncu ve kulüp {GAIN} PR kazandı.", "Kulüp Yönetimi"),
            "es" => new MailCopy("¡El directo del suplente se ha hecho viral!", "Hola,", "El último directo de {PLAYER} para {TEAM} se ha hecho viral. El jugador y el club han ganado {GAIN} puntos de PR.", "La dirección del club"),
            "fr" => new MailCopy("Le stream du remplaçant est devenu viral !", "Bonjour,", "Le dernier stream de {PLAYER} pour {TEAM} est devenu viral. Le joueur et le club ont gagné {GAIN} points de RP.", "La direction du club"),
            "ge" or "de" => new MailCopy("Der Stream des Ersatzspielers ging viral!", "Hallo,", "Der neueste Stream von {PLAYER} für {TEAM} ging viral. Der Spieler und der Club erhalten {GAIN} PR-Punkte.", "Die Vereinsleitung"),
            "hr" => new MailCopy("Prijenos pričuvnog igrača postao je viralan!", "Pozdrav,", "Najnoviji prijenos igrača {PLAYER} za klub {TEAM} postao je viralan. Igrač i klub dobili su {GAIN} PR bodova.", "Uprava kluba"),
            "hu" => new MailCopy("Vírusosan terjed a cserejátékos streamje!", "Üdvözlöm!", "{PLAYER} legutóbbi, {TEAM} színeiben tartott streamje vírusosan terjed. A játékos és a klub {GAIN} PR-pontot kapott.", "A klub vezetősége"),
            "it" => new MailCopy("La diretta della riserva è diventata virale!", "Ciao,", "L'ultima diretta di {PLAYER} per {TEAM} è diventata virale. Il giocatore e il club hanno guadagnato {GAIN} punti PR.", "La dirigenza del club"),
            "ja" => new MailCopy("控え選手の配信が話題になりました！", "こんにちは。", "{PLAYER}が{TEAM}のために行った最新の配信が話題になりました。選手とクラブはPRを{GAIN}獲得しました。", "クラブ運営部"),
            "kk" => new MailCopy("Қосалқы ойыншының стримі вирусқа айналды!", "Сәлеметсіз бе!", "{PLAYER} ойыншысының {TEAM} үшін өткізген соңғы стримі вирусқа айналды. Ойыншы мен клуб {GAIN} PR ұпайын алды.", "Клуб басшылығы"),
            "ko" => new MailCopy("후보 선수의 방송이 화제가 되었습니다!", "안녕하세요,", "{PLAYER} 선수가 {TEAM}을 위해 진행한 최신 방송이 큰 화제가 되었습니다. 선수와 구단이 PR {GAIN}을 얻었습니다.", "구단 운영진"),
            "nl" => new MailCopy("De stream van de reserve speler is viraal gegaan!", "Hallo,", "De nieuwste stream van {PLAYER} voor {TEAM} is viraal gegaan. De speler en de club hebben {GAIN} PR-punten verdiend.", "Het clubbestuur"),
            "pl" => new MailCopy("Stream rezerwowego stał się viralem!", "Witaj,", "Ostatni stream gracza {PLAYER} dla drużyny {TEAM} stał się viralem. Gracz i klub zdobyli {GAIN} punktów PR.", "Zarząd klubu"),
            "pr" or "pt" => new MailCopy("A transmissão do suplente tornou-se viral!", "Olá,", "A transmissão mais recente de {PLAYER} pela equipa {TEAM} tornou-se viral. O jogador e o clube ganharam {GAIN} pontos de RP.", "A direção do clube"),
            "ro" => new MailCopy("Streamul jucătorului de rezervă a devenit viral!", "Bună,", "Cel mai recent stream al lui {PLAYER} pentru {TEAM} a devenit viral. Jucătorul și clubul au câștigat {GAIN} puncte de PR.", "Conducerea clubului"),
            "ru" => new MailCopy("Стрим запасного игрока стал вирусным!", "Здравствуйте!", "Последний стрим игрока {PLAYER} для команды {TEAM} стал вирусным. Игрок и клуб получили {GAIN} очка PR.", "Руководство клуба"),
            "sk" => new MailCopy("Stream náhradníka sa stal virálnym!", "Dobrý deň,", "Najnovší stream hráča {PLAYER} pre tím {TEAM} sa stal virálnym. Hráč a klub získali {GAIN} bodov PR.", "Vedenie klubu"),
            "sl" => new MailCopy("Prenos rezervnega igralca je postal viralen!", "Pozdravljeni,", "Najnovejši prenos igralca {PLAYER} za ekipo {TEAM} je postal viralen. Igralec in klub sta pridobila {GAIN} točk PR.", "Vodstvo kluba"),
            "sv" => new MailCopy("Reservens stream har blivit viral!", "Hej,", "{PLAYER}s senaste stream för {TEAM} har blivit viral. Spelaren och klubben har fått {GAIN} PR-poäng.", "Klubbledningen"),
            "ua" or "uk" => new MailCopy("Стрим запасного гравця став вірусним!", "Вітаємо!", "Останній стрим гравця {PLAYER} для команди {TEAM} став вірусним. Гравець і клуб отримали {GAIN} очок PR.", "Керівництво клубу"),
            "zn" or "zh" => new MailCopy("替补选手的直播火了！", "您好：", "{PLAYER}为{TEAM}进行的最新直播走红了。该选手和俱乐部获得了{GAIN}点公关值。", "俱乐部管理层"),
            _ => new MailCopy("The club's match broadcast went viral!", "Hello,", "The match broadcast hosted by {PLAYER} for {TEAM} went viral. The player and the club gained {GAIN} PR.", "Club Management")
        };
    }

    private static string ReadLocaleCode()
    {
        Type settings = Plugin.FindLoadedType("LanguageSettings");
        object instance = settings?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        return instance == null ? "en" : ReadString(instance, "localeCode");
    }

    private static string Format(string text, string player, string team, int gain) =>
        text.Replace("{PLAYER}", player ?? "the player")
            .Replace("{TEAM}", team ?? "the club")
            .Replace("{GAIN}", gain.ToString(CultureInfo.InvariantCulture));

    private static object GetEntity(string databaseTypeName, string id)
    {
        Type database = Plugin.FindLoadedType(databaseTypeName);
        MethodInfo get = database?.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Get" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
        object entity = get?.Invoke(null, new object[] { id });
        if (entity != null || string.IsNullOrWhiteSpace(id))
            return entity;

        var candidateLists = new List<object>();
        if (database != null)
        {
            string[] members = string.Equals(databaseTypeName, "DataTeams", StringComparison.Ordinal)
                ? new[] { "List", "Teams", "AllTeams", "DataTeams" }
                : new[] { "List", "Players", "AllPlayers", "DataPlayers" };
            foreach (string member in members)
                candidateLists.Add(ReadStaticMember(database, member));
        }
        Type globalValues = Plugin.FindLoadedType("GlobalValues");
        if (globalValues != null)
        {
            string[] members = string.Equals(databaseTypeName, "DataTeams", StringComparison.Ordinal)
                ? new[] { "DataTeams", "Teams", "AllTeams" }
                : new[] { "DataPlayers", "Players", "AllPlayers" };
            foreach (string member in members)
                candidateLists.Add(ReadStaticMember(globalValues, member));
        }

        foreach (object teams in candidateLists)
        foreach (object team in ReadObjectsList(teams))
        {
            if (string.Equals(ReadString(team, "Id"), id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadString(team, "Nick"), id, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Instance?.Log.LogInfo($"Resolved team '{id}' to DataTeam ID '{ReadString(team, "Id")}' by name lookup.");
                return team;
            }
        }
        return null;
    }

    private static object ReadStaticMember(Type type, string member)
    {
        if (type == null)
            return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        return type.GetProperty(member, flags)?.GetValue(null)
            ?? type.GetField(member, flags)?.GetValue(null);
    }

    private static bool TryReadGameDate(object value, out DateTime date)
    {
        date = default;
        if (value == null)
            return false;

        if (value is DateTime systemDate)
        {
            date = systemDate;
            return true;
        }

        Type type = value.GetType();
        int year = ReadInt(value, "Year");
        int month = ReadInt(value, "Month");
        int day = ReadInt(value, "Day");
        if (year >= 1 && month >= 1 && month <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month))
        {
            date = new DateTime(year, month, day);
            return true;
        }

        return DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            || DateTime.TryParse(value.ToString(), CultureInfo.CurrentCulture, DateTimeStyles.None, out date);
    }

    private static int ReadInt(object target, string member) =>
        int.TryParse(ReadMember(target, member)?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static bool ReadBool(object target, string member) =>
        bool.TryParse(ReadMember(target, member)?.ToString(), out bool value) && value;

    private static string ReadString(object target, string member) => ReadMember(target, member)?.ToString();

    private static object ReadMember(object target, string member)
    {
        if (target == null)
            return null;
        Type type = target.GetType();
        return type.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target)
            ?? type.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
    }

    private static void SetMember(object target, string member, object value)
    {
        if (target == null || value == null)
            return;
        Type type = target.GetType();
        PropertyInfo property = type.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (property?.CanWrite == true && property.PropertyType.IsInstanceOfType(value))
            property.SetValue(target, value);
        else
        {
            FieldInfo field = type.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null && field.FieldType.IsInstanceOfType(value))
                field.SetValue(target, value);
        }
    }

    private static IEnumerable<string> ReadStringList(object list)
    {
        if (list is IEnumerable enumerable)
        {
            foreach (object item in enumerable)
                if (item != null)
                    yield return item.ToString();
            yield break;
        }

        Type type = list?.GetType();
        int count = ReadInt(list, "Count");
        PropertyInfo itemProperty = type?.GetProperty("Item");
        for (int i = 0; i < count; i++)
        {
            object item = itemProperty?.GetValue(list, new object[] { i });
            if (item != null)
                yield return item.ToString();
        }
    }

    private static IEnumerable<object> ReadObjectsList(object list)
    {
        if (list is IEnumerable enumerable)
        {
            foreach (object item in enumerable)
                if (item != null)
                    yield return item;
            yield break;
        }

        Type type = list?.GetType();
        int count = ReadInt(list, "Count");
        PropertyInfo itemProperty = type?.GetProperty("Item");
        for (int i = 0; i < count; i++)
        {
            object item = itemProperty?.GetValue(list, new object[] { i });
            if (item != null)
                yield return item;
        }
    }

    private static bool ContainsString(object list, string value)
    {
        MethodInfo contains = list?.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "Contains" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(string));
        if (contains != null)
            return Convert.ToBoolean(contains.Invoke(list, new object[] { value }), CultureInfo.InvariantCulture);
        return ReadStringList(list).Contains(value, StringComparer.Ordinal);
    }

    private sealed record MailCopy(string Subject, string Greeting, string Body, string Closing);
}
