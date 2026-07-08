using Il2CppInterop.Runtime.Attributes;
using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Object = UnityEngine.Object;

namespace AccuracyIndicator;

internal class Main : MelonMod
{
    internal static readonly System.Collections.Generic.List<NoteInfo> Notes = new();
    internal static CounterHUD HUD;
    internal static OsuPlayerHUD PlayerHUD;
    internal static int NextIdx;
    internal static bool Active;
    internal static float SongTime;
    internal static float RunStartRealtime;
    internal static float PauseStartRealtime;
    internal static float PausedRealtime;
    internal static int CntMonster;
    internal static int CntLong;
    internal static int CntBlock;
    internal static int CntMul;
    internal static int CntGhost;
    internal static int CntEnergy;
    internal static int CntMusic;
    internal static int CntBoss;
    internal static int CntAir;
    internal static int CntGround;
    internal static int CntUnknown;
    internal static float CurrentBpm;
    internal static float CurrentRuntimeBpm;

    [HarmonyPatch(typeof(StageBattleComponent), nameof(StageBattleComponent.GameStart))]
    private static class GameStartHook
    {
        private static void Postfix()
        {
            ResetRun("new game start", false, false);

            var sb = StageBattleComponent.instance;
            if (sb == null)
            {
                MelonLogger.Warning("[ManiaTest] GameStart ignored: StageBattleComponent.instance is null");
                return;
            }

            var arr = sb.GetMusicData();
            if (arr == null)
            {
                MelonLogger.Warning("[ManiaTest] GameStart ignored: GetMusicData returned null");
                return;
            }

            foreach (var md in arr)
            {
                if (md == null || md.noteData == null)
                    continue;

                float tick;
                try { tick = Decimal.ToSingle(md.tick); }
                catch { continue; }

                if (tick <= 0)
                    continue;

                int type = (int)md.noteData.type;
                float duration = ReadDuration(md, type);
                float endTime = duration > 0 ? tick + duration : 0;

                bool isAir = ReadBoolMember(md, "isAir") || ReadBoolMember(md, "IsAir");
                var multiHit = ReadMultiHitData(md, type, duration);
                Notes.Add(new NoteInfo(tick, endTime, type, isAir, multiHit.LowThreshold, multiHit.MidThreshold, multiHit.HighThreshold, multiHit.MaxHitCount));
            }

            Notes.Sort((a, b) => a.TimeSec.CompareTo(b.TimeSec));
            ReadCurrentBpm();
            NextIdx = 0;
            SongTime = 0;
            RunStartRealtime = Time.realtimeSinceStartup;
            PauseStartRealtime = 0;
            PausedRealtime = 0;
            ZeroCounters();
            Active = Notes.Count > 0;

            if (!Active)
            {
                MelonLogger.Warning("[ManiaTest] GameStart ignored: no playable notes were read");
                RefreshHud();
                return;
            }

            try
            {
                MapLoader.SaveCurrentMap(Notes, CurrentBpm, CurrentRuntimeBpm);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[ManiaTest] Failed to export map: {ex}");
            }

            IReadOnlyList<OsuPlayObject> playObjects = RuntimeOsuMapBuilder.Build(Notes, CurrentBpm);
            try
            {
                RuntimeOsuWriter.SaveLatest(playObjects, CurrentBpm);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[ManiaTest] Failed to export osu map: {ex}");
            }

            EnsurePlayerHud();
            try
            {
                if (HasLivePlayerHud())
                    PlayerHUD.LoadObjects(playObjects);
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[ManiaTest] Failed to load player map: {ex}");
            }

            MelonLogger.Msg($"[ManiaTest] Loaded {Notes.Count} notes, first={Notes[0].TimeSec:F2}s last={Notes[^1].TimeSec:F2}s bpm={CurrentBpm:F3} runtimeBpm={CurrentRuntimeBpm:F3}");
        }
    }

    [HarmonyPatch(typeof(PnlVictory), nameof(PnlVictory.OnVictory),
        typeof(Il2CppSystem.Object), typeof(Il2CppSystem.Object), typeof(Il2CppReferenceArray<Il2CppSystem.Object>))]
    private static class VictoryHook
    {
        private static void Postfix()
        {
            ResetRun("victory", true);
        }
    }

    [HarmonyPatch(typeof(PnlFail), nameof(PnlFail.Fail))]
    private static class FailHook
    {
        private static void Postfix()
        {
            ResetRun("fail", true);
        }
    }

    internal static void EnsureHud()
    {
        if (HasLiveHud())
            return;

        HUD = null;

        try
        {
            var go = new GameObject("ManiaTestCounterHUD");
            HUD = go.AddComponent<CounterHUD>();
        }
        catch (Exception ex)
        {
            HUD = null;
            MelonLogger.Error($"[ManiaTest] Failed to create HUD: {ex}");
        }
    }

    internal static void RefreshHud()
    {
        try
        {
            if (HasLiveHud())
                HUD.Refresh();
        }
        catch { }
    }

    internal static void EnsurePlayerHud()
    {
        if (HasLivePlayerHud())
            return;

        PlayerHUD = null;

        try
        {
            var go = new GameObject("ManiaInMuseOsuPlayerHUD");
            PlayerHUD = go.AddComponent<OsuPlayerHUD>();
        }
        catch (Exception ex)
        {
            PlayerHUD = null;
            MelonLogger.Error($"[ManiaTest] Failed to create osu player HUD: {ex}");
        }
    }

    internal static bool HasLivePlayerHud()
    {
        try
        {
            return PlayerHUD != null && PlayerHUD.gameObject != null;
        }
        catch
        {
            PlayerHUD = null;
            return false;
        }
    }

    internal static void DestroyPlayerHud()
    {
        if (!HasLivePlayerHud())
            return;

        try
        {
            if (PlayerHUD.gameObject != null)
                Object.Destroy(PlayerHUD.gameObject);
        }
        catch { }

        PlayerHUD = null;
    }

    internal static bool HasLiveHud()
    {
        try
        {
            return HUD != null && HUD.gameObject != null;
        }
        catch
        {
            HUD = null;
            return false;
        }
    }

    internal static void DestroyHud()
    {
        if (!HasLiveHud())
            return;

        try
        {
            if (HUD.gameObject != null)
                Object.Destroy(HUD.gameObject);
        }
        catch { }

        HUD = null;
    }

    internal static void ResetRun(string reason, bool logEnd, bool destroyHud = true)
    {
        if (logEnd && (Active || Notes.Count > 0))
        {
            MelonLogger.Msg($"[ManiaTest] End: G{CntGround} A{CntAir} Hold{CntLong} Block{CntBlock} Mul{CntMul} Boss{CntBoss} Gh{CntGhost} En{CntEnergy} Ms{CntMusic} U{CntUnknown} / {Notes.Count} t={SongTime:F1}s ({reason})");
        }

        Active = false;
        Notes.Clear();
        NextIdx = 0;
        SongTime = 0;
        RunStartRealtime = 0;
        PauseStartRealtime = 0;
        PausedRealtime = 0;
        CurrentBpm = 0;
        CurrentRuntimeBpm = 0;
        ZeroCounters();

        if (destroyHud)
        {
            DestroyHud();
            DestroyPlayerHud();
        }
        else
        {
            RefreshHud();
        }
    }

    internal static void UpdatePlaybackState()
    {
        if (!Active)
            return;

        var sb = StageBattleComponent.instance;
        if (sb == null)
            return;

        if (sb.isInGame)
        {
            if (PauseStartRealtime > 0)
            {
                PausedRealtime += Time.realtimeSinceStartup - PauseStartRealtime;
                PauseStartRealtime = 0;
            }

            SongTime = GetCurrentSongTime();
        }
        else
        {
            if (PauseStartRealtime <= 0)
                PauseStartRealtime = Time.realtimeSinceStartup;
            return;
        }

        while (NextIdx < Notes.Count)
        {
            if (Notes[NextIdx].TimeSec > SongTime + 0.06f)
                break;

            var note = Notes[NextIdx];
            switch (note.Type)
            {
                case 1:
                    CntMonster++;
                    if (note.IsAir)
                        CntAir++;
                    else
                        CntGround++;
                    break;
                case 2:
                    CntBlock++;
                    break;
                case 3:
                    CntLong++;
                    break;
                case 4:
                    CntGhost++;
                    break;
                case 5:
                    CntBoss++;
                    break;
                case 6:
                    CntEnergy++;
                    break;
                case 7:
                    CntMusic++;
                    break;
                case 8:
                    CntMul++;
                    break;
                default:
                    CntUnknown++;
                    break;
            }

            NextIdx++;
        }
    }

    internal static void ZeroCounters()
    {
        CntMonster = 0;
        CntLong = 0;
        CntBlock = 0;
        CntMul = 0;
        CntGhost = 0;
        CntEnergy = 0;
        CntMusic = 0;
        CntBoss = 0;
        CntAir = 0;
        CntGround = 0;
        CntUnknown = 0;
    }

    internal static float GetCurrentSongTime()
    {
        float paused = PausedRealtime;
        if (PauseStartRealtime > 0)
            paused += Time.realtimeSinceStartup - PauseStartRealtime;

        return Time.realtimeSinceStartup - RunStartRealtime - paused;
    }

    private static bool ReadBoolMember(object obj, string name)
    {
        try
        {
            var type = obj.GetType();
            var prop = type.GetProperty(name);
            if (prop != null && prop.PropertyType == typeof(bool))
                return (bool)prop.GetValue(obj);

            var field = type.GetField(name);
            if (field != null && field.FieldType == typeof(bool))
                return (bool)field.GetValue(obj);
        }
        catch { }

        return false;
    }

    private static float ReadDuration(MusicData md, int type)
    {
        if (type is not 3 and not 8)
            return 0;

        try
        {
            if (md.configData != null)
                return Decimal.ToSingle(md.configData.length);
        }
        catch { }

        return 0;
    }

    private static void ReadCurrentBpm()
    {
        CurrentBpm = 0;
        CurrentRuntimeBpm = 0;

        try
        {
            var stageInfo = AccessTools.Property(typeof(GlobalDataBase), "dbStageInfo")?.GetValue(null)
                ?? AccessTools.Property(typeof(GlobalDataBase), "s_StageInfo")?.GetValue(null);

            CurrentBpm = ReadFloatMember(stageInfo, "bpm");
            CurrentRuntimeBpm = ReadFloatMember(stageInfo, "runtimeBpm");

            if (CurrentRuntimeBpm <= 0)
                CurrentRuntimeBpm = ReadFloatMember(stageInfo, "m_Bpm");
            if (CurrentBpm <= 0)
                CurrentBpm = CurrentRuntimeBpm;
            if (CurrentRuntimeBpm <= 0)
                CurrentRuntimeBpm = CurrentBpm;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[ManiaTest] Failed to read BPM: {ex.Message}");
        }
    }

    private static float ReadFloatMember(object obj, string name)
    {
        if (obj == null)
            return 0;

        try
        {
            var type = obj.GetType();
            var prop = type.GetProperty(name);
            if (prop != null)
                return ConvertToFloat(prop.GetValue(obj));

            var field = type.GetField(name);
            if (field != null)
                return ConvertToFloat(field.GetValue(obj));
        }
        catch { }

        return 0;
    }

    private static float ConvertToFloat(object value)
    {
        try
        {
            return value switch
            {
                null => 0,
                float f => f,
                double d => (float)d,
                int i => i,
                string s when float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) => f,
                _ => System.Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture)
            };
        }
        catch
        {
            return 0;
        }
    }

    private static MultiHitData ReadMultiHitData(MusicData md, int type, float durationSec)
    {
        bool isMulti = type == 8;
        try { isMulti |= md.isMul; } catch { }

        if (!isMulti)
            return MultiHitData.Empty;

        int low = 0;
        int mid = 0;
        int high = 0;

        try { low = md.GetMulHitLowThreshold(); } catch { }
        try { mid = md.GetMulHitMidThreshold(); } catch { }
        try { high = md.GetMulHitHighThreshold(); } catch { }

        int max = high > 0 ? high : Math.Max(low, mid);
        return new MultiHitData(low, mid, high, max, durationSec);
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
        if (sceneName == "GameMain")
            ResetRun("GameMain unloaded", true);
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        if (sceneName == "GameMain" && Active)
        {
            MelonLogger.Msg("[ManiaTest] Reset stale run on GameMain load");
            ResetRun("stale GameMain load", true);
        }
    }
}

public class NoteInfo
{
    public float TimeSec;
    public float EndTimeSec;
    public int Type;
    public bool IsAir;
    public int MultiLowThreshold;
    public int MultiMidThreshold;
    public int MultiHighThreshold;
    public int MultiMaxHitCount;
    public float MultiDurationSec;

    public NoteInfo(float timeSec, float endTimeSec, int type, bool isAir, int multiLowThreshold, int multiMidThreshold, int multiHighThreshold, int multiMaxHitCount)
    {
        TimeSec = timeSec;
        EndTimeSec = endTimeSec;
        Type = type;
        IsAir = isAir;
        MultiLowThreshold = multiLowThreshold;
        MultiMidThreshold = multiMidThreshold;
        MultiHighThreshold = multiHighThreshold;
        MultiMaxHitCount = multiMaxHitCount;
        MultiDurationSec = type == 8 ? Math.Max(0, endTimeSec - timeSec) : 0;
    }
}

internal readonly struct MultiHitData
{
    internal static readonly MultiHitData Empty = new(0, 0, 0, 0, 0);

    internal readonly int LowThreshold;
    internal readonly int MidThreshold;
    internal readonly int HighThreshold;
    internal readonly int MaxHitCount;
    internal readonly float DurationSec;

    internal MultiHitData(int lowThreshold, int midThreshold, int highThreshold, int maxHitCount, float durationSec)
    {
        LowThreshold = lowThreshold;
        MidThreshold = midThreshold;
        HighThreshold = highThreshold;
        MaxHitCount = maxHitCount;
        DurationSec = durationSec;
    }
}

[RegisterTypeInIl2Cpp]
public class CounterHUD : MonoBehaviour
{
    private const int CounterSortingOrder = 950;

    private Text _text;
    private bool _dead;

    public CounterHUD(IntPtr ptr) : base(ptr) { }

    private void Start()
    {
        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = CounterSortingOrder;

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        gameObject.AddComponent<GraphicRaycaster>();

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(gameObject.transform, false);

        var rect = textGo.AddComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(620, 300);

        _text = textGo.AddComponent<Text>();
        _text.font = Font.CreateDynamicFontFromOSFont("Arial", 20);
        _text.fontSize = 32;
        _text.color = Color.white;
        _text.alignment = TextAnchor.MiddleCenter;
        Refresh();
    }

    private void Update()
    {
        if (_dead || _text == null)
            return;

        if (!Main.Active)
        {
            _text.text = "";
            return;
        }

        Main.UpdatePlaybackState();
        Refresh();
    }

    public void Refresh()
    {
        if (_dead || _text == null)
            return;

        if (!Main.Active)
        {
            _text.text = "";
            return;
        }

        int sum = Main.CntMonster + Main.CntLong + Main.CntBlock + Main.CntMul + Main.CntBoss + Main.CntGhost + Main.CntEnergy + Main.CntMusic + Main.CntUnknown;
        _text.text = $"G:{Main.CntGround} A:{Main.CntAir}  Hold:{Main.CntLong}\nBlock:{Main.CntBlock} Mul:{Main.CntMul} Boss:{Main.CntBoss} Gh:{Main.CntGhost}\nEn:{Main.CntEnergy} Ms:{Main.CntMusic} U:{Main.CntUnknown}\nTick:{Main.SongTime:F1}s\n-------------\nTotal: {sum} / {Main.Notes.Count}";
    }

    private void OnDestroy()
    {
        _dead = true;
        _text = null;

        if (Main.HUD == this)
            Main.HUD = null;
    }
}
