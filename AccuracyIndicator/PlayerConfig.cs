using System.Globalization;
using System.Text;

namespace AccuracyIndicator;

internal sealed class PlayerConfig
{
    internal const string ConfigPath = "UserData\\ManiaInMuse\\Player.cfg";
    private const int MinKeyCount = 2;
    private const int MaxKeyCount = 7;
    private const int DefaultKeyCount = 4;

    internal float OffsetMs { get; private set; }
    internal float FallTimeMs { get; private set; } = 480f;
    internal float TrackWidth { get; private set; } = 480f;
    internal float TrackHeight { get; private set; } = 1080f;
    internal float NoteWidth { get; private set; } = 120f;
    internal float NoteHeight { get; private set; } = 80f;
    internal float JudgementLinePosition { get; private set; } = 1f;
    internal float PositionX { get; private set; }
    internal float PositionY { get; private set; }
    internal Color32 BackgroundColor { get; private set; } = new(0, 0, 0, 255);
    internal Color32 NoteColor { get; private set; } = new(0, 220, 70, 255);
    internal Color32 HoldColor { get; private set; } = new(110, 110, 110, 255);
    internal bool AutoCleanCache { get; private set; } = true;
    internal int CacheMaxMapFiles { get; private set; } = 20;
    internal bool EnableLocalSwapOptimizer { get; private set; } = true;
    internal int OptimizerChordWindowMs { get; private set; } = 12;
    internal int OptimizerMinTriggerChordCount { get; private set; } = 2;
    internal int OptimizerMinConsecutiveChords { get; private set; } = 2;
    internal bool EnableShortGapRepair { get; private set; } = true;
    internal int ShortGapTargetMs { get; private set; } = 200;
    internal int ShortGapHardMinMs { get; private set; } = 100;
    internal int ShortGapSegmentPaddingMs { get; private set; } = 400;
    internal int ShortGapSegmentBreakMs { get; private set; } = 500;
    internal IReadOnlyList<LaneDefinition> Lanes { get; private set; } = BuildLanes(DefaultKeyCount, DefaultPosturesFor(DefaultKeyCount), null);

    internal float OffsetSec => OffsetMs / 1000f;
    internal float FallTimeSec => Math.Max(1f, FallTimeMs) / 1000f;
    internal float OptimizerChordWindowSec => OptimizerChordWindowMs / 1000f;
    internal float ShortGapTargetSec => ShortGapTargetMs / 1000f;
    internal float ShortGapHardMinSec => ShortGapHardMinMs / 1000f;
    internal float ShortGapSegmentPaddingSec => ShortGapSegmentPaddingMs / 1000f;
    internal float ShortGapSegmentBreakSec => ShortGapSegmentBreakMs / 1000f;
    internal int KeyCount => Lanes.Count;
    internal int[] AllLaneIndexes => Lanes.Select(l => l.Index).ToArray();
    internal int[] AirLaneIndexes => Lanes.Where(l => l.Posture == LanePosture.Air).Select(l => l.Index).ToArray();
    internal int[] GroundLaneIndexes => Lanes.Where(l => l.Posture == LanePosture.Ground).Select(l => l.Index).ToArray();

    private int _keyCount = DefaultKeyCount;
    private int _leftLaneCount = DefaultLeftLaneCount(DefaultKeyCount);
    private int? _activeLeftLaneCount;
    private LanePosture[] _lanePostures;
    private readonly Dictionary<int, LanePosture[]> _profilePostures = new();
    private readonly Dictionary<int, int> _profileLeftCounts = new();

    internal static PlayerConfig LoadOrCreate()
    {
        EnsureDefaultFile();

        var config = new PlayerConfig();
        if (!File.Exists(ConfigPath))
            return config;

        int? sectionKeyCount = null;
        foreach (string rawLine in File.ReadAllLines(ConfigPath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                sectionKeyCount = TryReadKeysSection(line, out int keysSectionCount) ? keysSectionCount : null;
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (sectionKeyCount.HasValue)
                config.ApplyKeysSection(sectionKeyCount.Value, key, value);
            else
                config.Apply(key, value);
        }

        config.TrackWidth = Math.Max(60f, config.TrackWidth);
        config.TrackHeight = Math.Max(120f, config.TrackHeight);
        config.NoteWidth = Math.Max(1f, config.NoteWidth);
        config.NoteHeight = Math.Max(1f, config.NoteHeight);
        config.JudgementLinePosition = Math.Clamp(config.JudgementLinePosition, 0f, 1f);
        config.OffsetMs = float.IsFinite(config.OffsetMs) ? Math.Clamp(config.OffsetMs, -1000f, 1000f) : 0f;
        config.FallTimeMs = Math.Max(1f, config.FallTimeMs);
        config.CacheMaxMapFiles = Math.Max(0, config.CacheMaxMapFiles);
        config.OptimizerChordWindowMs = Math.Clamp(config.OptimizerChordWindowMs, 1, 50);
        config.OptimizerMinTriggerChordCount = Math.Clamp(config.OptimizerMinTriggerChordCount, 2, MaxKeyCount);
        config.OptimizerMinConsecutiveChords = Math.Clamp(config.OptimizerMinConsecutiveChords, 2, 16);
        config.ShortGapHardMinMs = Math.Clamp(config.ShortGapHardMinMs, 50, 180);
        config.ShortGapTargetMs = Math.Clamp(config.ShortGapTargetMs, config.ShortGapHardMinMs, 350);
        config.ShortGapSegmentPaddingMs = Math.Clamp(config.ShortGapSegmentPaddingMs, 100, 1200);
        config.ShortGapSegmentBreakMs = Math.Clamp(config.ShortGapSegmentBreakMs, 100, 1500);
        config.NormalizeLaneConfig();
        return config;
    }

    private void Apply(string key, string value)
    {
        string lowerKey = key.ToLowerInvariant();
        if (TryGetProfileCount(lowerKey, out int profileKeyCount, "lanetypes", "lanepostures"))
        {
            if (!IsSupportedKeyCount(profileKeyCount))
            {
                MelonLogger.Warning($"[ManiaInMuse] Ignoring lane posture profile for unsupported key count {profileKeyCount}; valid range is {MinKeyCount}-{MaxKeyCount}");
                return;
            }

            if (TryReadLanePostures(value, out var profilePostures) && profilePostures.Length == profileKeyCount)
                _profilePostures[profileKeyCount] = profilePostures;
            else
                MelonLogger.Warning($"[ManiaInMuse] Ignoring LaneTypes{profileKeyCount}; it must contain exactly {profileKeyCount} Air/Ground values");
            return;
        }

        if (TryGetProfileCount(lowerKey, out profileKeyCount, "leftlanecount", "splitleftcount", "laneleftcount"))
        {
            if (!IsSupportedKeyCount(profileKeyCount))
            {
                MelonLogger.Warning($"[ManiaInMuse] Ignoring split profile for unsupported key count {profileKeyCount}; valid range is {MinKeyCount}-{MaxKeyCount}");
                return;
            }

            if (TryReadInt(value, out int profileLeftCount))
                _profileLeftCounts[profileKeyCount] = profileLeftCount;
            return;
        }

        switch (lowerKey)
        {
            case "keycount":
            case "lanecount":
                if (TryReadInt(value, out int keyCount))
                    _keyCount = keyCount;
                return;
            case "lanetypes":
            case "lanepostures":
                if (TryReadLanePostures(value, out var postures))
                    _lanePostures = postures;
                return;
            case "leftlanecount":
            case "splitleftcount":
            case "laneleftcount":
                if (TryReadInt(value, out int leftCount))
                    _activeLeftLaneCount = leftCount;
                return;
            case "cachemaxmapfiles":
            case "cachemaxfiles":
            case "maxcachefiles":
                if (TryReadInt(value, out int maxFiles))
                    CacheMaxMapFiles = maxFiles;
                return;
            case "optimizerchordwindowms":
            case "chordwindowms":
            case "chordwindows":
                if (TryReadInt(value, out int chordWindowMs))
                    OptimizerChordWindowMs = chordWindowMs;
                return;
            case "optimizermintriggerchordcount":
            case "mintriggerchordcount":
            case "triggerchordcount":
                if (TryReadInt(value, out int triggerChordCount))
                    OptimizerMinTriggerChordCount = triggerChordCount;
                return;
            case "optimizerminconsecutivechords":
            case "minconsecutivechords":
            case "consecutivechords":
                if (TryReadInt(value, out int consecutiveChords))
                    OptimizerMinConsecutiveChords = consecutiveChords;
                return;
            case "shortgaptargetms":
            case "shortgaptarget":
            case "targetgapms":
                if (TryReadInt(value, out int targetGapMs))
                    ShortGapTargetMs = targetGapMs;
                return;
            case "shortgaphardminms":
            case "hardmingapms":
            case "mingapms":
                if (TryReadInt(value, out int hardMinGapMs))
                    ShortGapHardMinMs = hardMinGapMs;
                return;
            case "shortgapsegmentpaddingms":
            case "segmentpaddingms":
            case "repairpaddingms":
                if (TryReadInt(value, out int paddingMs))
                    ShortGapSegmentPaddingMs = paddingMs;
                return;
            case "shortgapsegmentbreakms":
            case "segmentbreakms":
            case "repairbreakms":
                if (TryReadInt(value, out int breakMs))
                    ShortGapSegmentBreakMs = breakMs;
                return;
        }

        if (TryReadBool(value, out bool boolean))
        {
            switch (lowerKey)
            {
                case "autocleancache":
                case "cleancache":
                case "enablecachecleanup":
                    AutoCleanCache = boolean;
                    return;
                case "enablelocalswap":
                case "enablelocalswapoptimizer":
                case "localswapoptimizer":
                    EnableLocalSwapOptimizer = boolean;
                    return;
                case "enableshortgaprepair":
                case "shortgaprepair":
                case "repairshortgaps":
                    EnableShortGapRepair = boolean;
                    return;
            }
        }

        if (TryReadFloat(value, out float number))
        {
            switch (lowerKey)
            {
                case "offsetms":
                    OffsetMs = number;
                    return;
                case "falltimems":
                case "falltime":
                    FallTimeMs = number;
                    return;
                case "trackwidth":
                case "width":
                    TrackWidth = number;
                    return;
                case "trackheight":
                case "height":
                    TrackHeight = number;
                    return;
                case "notewidth":
                case "notewide":
                case "keywidth":
                case "clickwidth":
                    NoteWidth = number;
                    return;
                case "noteheight":
                case "notehight":
                case "keyheight":
                case "clickheight":
                    NoteHeight = number;
                    return;
                case "judgementlineposition":
                case "judgmentlineposition":
                case "judgelineposition":
                case "lineposition":
                    JudgementLinePosition = number;
                    return;
                case "positionx":
                case "x":
                    PositionX = number;
                    return;
                case "positiony":
                case "y":
                    PositionY = number;
                    return;
            }
        }

        if (TryReadColor(value, out Color32 color))
        {
            switch (lowerKey)
            {
                case "backgroundcolor":
                case "background":
                    BackgroundColor = color;
                    return;
                case "notecolor":
                case "keycolor":
                    NoteColor = color;
                    return;
                case "holdcolor":
                case "longnotecolor":
                    HoldColor = color;
                    return;
            }
        }
    }

    private void ApplyKeysSection(int keyCount, string key, string value)
    {
        if (!IsSupportedKeyCount(keyCount))
        {
            MelonLogger.Warning($"[ManiaInMuse] Ignoring [keys:{keyCount}]; valid range is {MinKeyCount}-{MaxKeyCount}");
            return;
        }

        string lowerKey = key.ToLowerInvariant();
        if (lowerKey is "lanetypes" or "lanepostures" or "layout" or "types")
        {
            if (TryReadLanePostures(value, out var postures) && postures.Length == keyCount)
                _profilePostures[keyCount] = postures;
            else
                MelonLogger.Warning($"[ManiaInMuse] Ignoring [keys:{keyCount}] {key}; it must contain exactly {keyCount} A/G values");
            return;
        }

        if (lowerKey is "split" or "splitpoint" or "leftlanecount" or "splitleftcount")
        {
            if (TryReadInt(value, out int leftCount))
                _profileLeftCounts[keyCount] = leftCount;
            return;
        }
    }

    internal bool IsAirLane(int lane)
    {
        return GetLane(lane).Posture == LanePosture.Air;
    }

    internal bool IsLeftSide(int lane)
    {
        var ordered = Lanes
            .OrderBy(l => l.OsuX)
            .ThenBy(l => l.Index)
            .ToList();
        int leftCount = Math.Clamp(_leftLaneCount, 1, Math.Max(1, ordered.Count - 1));
        for (int i = 0; i < leftCount; i++)
        {
            if (ordered[i].Index == lane)
                return true;
        }

        return false;
    }

    internal int LaneToX(int lane)
    {
        return GetLane(lane).OsuX;
    }

    internal float LaneToPlayfieldX(int lane, float playfieldWidth)
    {
        return LaneToX(lane) / 512f * playfieldWidth - playfieldWidth * 0.5f;
    }

    internal int XToLane(int x)
    {
        int bestLane = Lanes[0].Index;
        int bestDistance = int.MaxValue;
        foreach (var lane in Lanes)
        {
            int distance = Math.Abs(lane.OsuX - x);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestLane = lane.Index;
            }
        }

        return bestLane;
    }

    internal int[] LaneIndexesFor(LanePosture posture)
    {
        return posture == LanePosture.Air ? AirLaneIndexes : GroundLaneIndexes;
    }

    private LaneDefinition GetLane(int lane)
    {
        foreach (var definition in Lanes)
        {
            if (definition.Index == lane)
                return definition;
        }

        return Lanes[Math.Clamp(lane, 1, Lanes.Count) - 1];
    }

    private void NormalizeLaneConfig()
    {
        if (!IsSupportedKeyCount(_keyCount))
        {
            MelonLogger.Warning($"[ManiaInMuse] KeyCount {_keyCount} is outside valid range {MinKeyCount}-{MaxKeyCount}; using {DefaultKeyCount}");
            _keyCount = DefaultKeyCount;
        }

        LanePosture[] postures = ResolvePostures(_keyCount);
        _leftLaneCount = ResolveLeftLaneCount(_keyCount);

        Lanes = BuildLanes(_keyCount, postures, null);
    }

    private static IReadOnlyList<LaneDefinition> BuildLanes(int keyCount, LanePosture[] postures, int[] positions)
    {
        int[] resolvedPositions = positions != null && positions.Length == keyCount
            ? positions.Select(p => Math.Clamp(p, 0, 511)).ToArray()
            : AutoPositions(keyCount);

        var lanes = new List<LaneDefinition>(keyCount);
        for (int i = 0; i < keyCount; i++)
            lanes.Add(new LaneDefinition(i + 1, postures[i], resolvedPositions[i]));

        return lanes;
    }

    private LanePosture[] ResolvePostures(int keyCount)
    {
        LanePosture[] postures = null;
        if (_lanePostures != null && _lanePostures.Length == keyCount)
            postures = _lanePostures;
        else if (_profilePostures.TryGetValue(keyCount, out var profilePostures) && profilePostures.Length == keyCount)
            postures = profilePostures;

        postures ??= DefaultPosturesFor(keyCount);
        if (!postures.Any(p => p == LanePosture.Air) || !postures.Any(p => p == LanePosture.Ground))
        {
            MelonLogger.Warning($"[ManiaInMuse] LaneTypes for {keyCount}K must include at least one Air and one Ground lane; using default");
            postures = DefaultPosturesFor(keyCount);
        }

        return postures.ToArray();
    }

    private int ResolveLeftLaneCount(int keyCount)
    {
        int leftCount = _activeLeftLaneCount
            ?? (_profileLeftCounts.TryGetValue(keyCount, out int profileLeftCount) ? profileLeftCount : DefaultLeftLaneCount(keyCount));

        if (leftCount < 1 || leftCount >= keyCount)
        {
            MelonLogger.Warning($"[ManiaInMuse] LeftLaneCount for {keyCount}K must be between 1 and {keyCount - 1}; using default");
            return DefaultLeftLaneCount(keyCount);
        }

        return leftCount;
    }

    private static LanePosture[] DefaultPosturesFor(int keyCount)
    {
        if (keyCount == 6)
            return [LanePosture.Air, LanePosture.Air, LanePosture.Ground, LanePosture.Ground, LanePosture.Air, LanePosture.Ground];
        if (keyCount == 4)
            return [LanePosture.Air, LanePosture.Ground, LanePosture.Air, LanePosture.Ground];

        var postures = new LanePosture[keyCount];
        for (int i = 0; i < keyCount; i++)
            postures[i] = i % 2 == 0 ? LanePosture.Air : LanePosture.Ground;
        return postures;
    }

    private static int DefaultLeftLaneCount(int keyCount)
    {
        return Math.Clamp(keyCount / 2, 1, Math.Max(1, keyCount - 1));
    }

    private static bool IsSupportedKeyCount(int keyCount)
    {
        return keyCount >= MinKeyCount && keyCount <= MaxKeyCount;
    }

    private static int[] AutoPositions(int keyCount)
    {
        var positions = new int[keyCount];
        for (int i = 0; i < keyCount; i++)
            positions[i] = (int)Math.Floor((i + 0.5) * 512 / keyCount);
        return positions;
    }

    private static void EnsureDefaultFile()
    {
        try
        {
            string directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (!File.Exists(ConfigPath))
            {
                File.WriteAllText(ConfigPath,
                    """
                    [Player]
                    # Visual timing offset in milliseconds, range -1000 to 1000. Positive values make notes fall later.
                    OffsetMs = 0

                    # Time from note spawn at top to judgement line, in milliseconds.
                    FallTimeMs = 480

                    # Track rectangle size in 1920x1080 canvas coordinates.
                    TrackWidth = 480
                    TrackHeight = 1080

                    # Click note size. Hold heads use the same size; hold bodies use NoteWidth.
                    NoteWidth = 120
                    NoteHeight = 80

                    # Track center offset from screen center.
                    PositionX = 0
                    PositionY = 0

                    # Colors are R,G,B or R,G,B,A, range 0-255.
                    BackgroundColor = 0,0,0,255
                    NoteColor = 0,220,70,255
                    HoldColor = 110,110,110,255

                    # Judgement line position within the track: 0 = top, 0.5 = center, 1 = bottom.
                    JudgementLinePosition = 1

                    # Key count, valid range: 2-7.
                    KeyCount = 4

                    # Per-key lane posture presets. A = air, G = ground.
                    [keys:2]
                    LaneTypes = A,G
                    Split = 1

                    [keys:3]
                    LaneTypes = A,G,A
                    Split = 1

                    [keys:4]
                    LaneTypes = A,G,A,G
                    Split = 2

                    [keys:5]
                    LaneTypes = A,G,A,G,A
                    Split = 2

                    [keys:6]
                    LaneTypes = A,A,G,G,A,G
                    Split = 3

                    [keys:7]
                    LaneTypes = A,G,A,G,A,G,A
                    Split = 3
                    """);
            }

            EnsureOffsetOption();
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[ManiaInMuse] Failed to create player config: {ex.Message}");
        }
    }

    private static void EnsureOffsetOption()
    {
        var lines = File.ReadAllLines(ConfigPath).ToList();
        if (lines.Any(line =>
            {
                int separator = line.IndexOf('=');
                return separator > 0
                    && line[..separator].Trim().Equals("OffsetMs", StringComparison.OrdinalIgnoreCase);
            }))
            return;

        int playerSection = lines.FindIndex(line => line.Trim().Equals("[Player]", StringComparison.OrdinalIgnoreCase));
        int insertAt = playerSection >= 0 ? playerSection + 1 : 0;
        lines.Insert(insertAt, "# Visual timing offset in milliseconds, range -1000 to 1000. Positive values make notes fall later.");
        lines.Insert(insertAt + 1, "OffsetMs = 0");
        lines.Insert(insertAt + 2, "");
        File.WriteAllLines(ConfigPath, lines, new UTF8Encoding(false));
    }

    private static bool TryReadFloat(string value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryReadInt(string value, out int result)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static bool TryReadBool(string value, out bool result)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "true":
            case "1":
            case "yes":
            case "on":
                result = true;
                return true;
            case "false":
            case "0":
            case "no":
            case "off":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static bool TryReadIntList(string value, out int[] result)
    {
        result = Array.Empty<int>();
        string[] parts = value.Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }

        result = values;
        return true;
    }

    private static bool TryGetProfileCount(string key, out int keyCount, params string[] prefixes)
    {
        keyCount = 0;
        foreach (string prefix in prefixes)
        {
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            string suffix = key[prefix.Length..];
            return suffix.Length > 0 && int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out keyCount);
        }

        return false;
    }

    private static bool TryReadKeysSection(string line, out int keyCount)
    {
        keyCount = 0;
        string section = line.Trim('[', ']', ' ', '\t').ToLowerInvariant();
        if (!section.StartsWith("keys:", StringComparison.Ordinal))
            return false;

        return int.TryParse(section["keys:".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out keyCount);
    }

    private static bool TryReadLanePostures(string value, out LanePosture[] postures)
    {
        postures = Array.Empty<LanePosture>();
        string[] parts = value.Split([',', ';', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var result = new LanePosture[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].ToLowerInvariant();
            if (part is "air" or "a" or "sky" or "up")
                result[i] = LanePosture.Air;
            else if (part is "ground" or "g" or "land" or "down")
                result[i] = LanePosture.Ground;
            else
                return false;
        }

        postures = result;
        return true;
    }

    private static bool TryReadColor(string value, out Color32 color)
    {
        color = default;
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not 3 and not 4)
            return false;

        byte[] values = new byte[4] { 0, 0, 0, 255 };
        for (int i = 0; i < parts.Length; i++)
        {
            if (!byte.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                return false;
        }

        color = new Color32(values[0], values[1], values[2], values[3]);
        return true;
    }
}

internal enum LanePosture
{
    Ground,
    Air
}

internal readonly struct LaneDefinition
{
    internal readonly int Index;
    internal readonly LanePosture Posture;
    internal readonly int OsuX;

    internal LaneDefinition(int index, LanePosture posture, int osuX)
    {
        Index = index;
        Posture = posture;
        OsuX = osuX;
    }
}
