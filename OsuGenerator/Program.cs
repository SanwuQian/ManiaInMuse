using System.Globalization;
using System.Text;

namespace ManiaInMuse.OsuGenerator;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = GeneratorOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(GeneratorOptions.HelpText);
                return 0;
            }

            var map = CsvMapReader.Read(options.InputPath);
            options.ApplyMapMetadata(map);
            var beatmap = ManiaConverter.Convert(map.Notes, options);
            OsuWriter.Write(beatmap, options);

            Console.WriteLine($"Read {map.Notes.Count} csv notes.");
            Console.WriteLine($"BPM {options.Bpm.ToString("0.###", CultureInfo.InvariantCulture)}.");
            Console.WriteLine($"Wrote {beatmap.Objects.Count} osu hit objects.");
            Console.WriteLine(options.OutputPath);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}

internal sealed class GeneratorOptions
{
    internal string InputPath { get; private init; } = "";
    internal string OutputPath { get; private set; } = "";
    internal string Title { get; private init; } = "Muse Dash Converted Map";
    internal string Artist { get; private init; } = "PeroPeroGames";
    internal string Creator { get; private init; } = "ManiaInMuse";
    internal string Version { get; private init; } = "Converted";
    internal string AudioFileName { get; private init; } = "audio.mp3";
    internal double Bpm { get; private set; } = 120;
    internal bool BpmSpecified { get; private init; }
    internal bool ShowHelp { get; private init; }

    internal const string HelpText =
        """
        ManiaInMuse.OsuGenerator

        Usage:
          dotnet run --project OsuGenerator -- <input.csv> [output.osu] [options]

        Options:
          --bpm <value>       Override BPM used for timing points and multi pattern. CSV BPM is used by default when present.
          --title <value>     Beatmap title.
          --artist <value>    Beatmap artist.
          --creator <value>   Beatmap creator.
          --version <value>   Beatmap difficulty/version name.
          --audio <file>      Audio filename stored in the .osu file. Default: audio.mp3
          -h, --help          Show help.
        """;

    internal static GeneratorOptions Parse(string[] args)
    {
        if (args.Length == 0 || args.Any(a => a is "-h" or "--help"))
            return new GeneratorOptions { ShowHelp = true };

        string input = "";
        string output = "";
        string title = "Muse Dash Converted Map";
        string artist = "PeroPeroGames";
        string creator = "ManiaInMuse";
        string version = "Converted";
        string audio = "audio.mp3";
        double bpm = 120;
        bool bpmSpecified = false;

        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            string ReadValue()
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {arg}");
                return args[++i];
            }

            switch (arg)
            {
                case "--bpm":
                    if (!double.TryParse(ReadValue(), NumberStyles.Float, CultureInfo.InvariantCulture, out bpm) || bpm <= 0)
                        throw new ArgumentException("--bpm must be a positive number.");
                    bpmSpecified = true;
                    break;
                case "--title":
                    title = ReadValue();
                    break;
                case "--artist":
                    artist = ReadValue();
                    break;
                case "--creator":
                    creator = ReadValue();
                    break;
                case "--version":
                    version = ReadValue();
                    break;
                case "--audio":
                    audio = ReadValue();
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        if (positional.Count == 0)
            throw new ArgumentException("Input csv path is required.");
        if (positional.Count > 2)
            throw new ArgumentException("Only input and optional output paths are accepted.");

        input = positional[0];
        output = positional.Count == 2
            ? positional[1]
            : Path.ChangeExtension(input, ".osu");

        return new GeneratorOptions
        {
            InputPath = input,
            OutputPath = output,
            Title = title,
            Artist = artist,
            Creator = creator,
            Version = version,
            AudioFileName = audio,
            Bpm = bpm,
            BpmSpecified = bpmSpecified
        };
    }

    internal void ApplyMapMetadata(CsvMap map)
    {
        if (!BpmSpecified && map.Bpm > 0)
            Bpm = map.Bpm;
    }
}

internal sealed record CsvMap(IReadOnlyList<CsvNote> Notes, double Bpm, double RuntimeBpm);

internal sealed record CsvNote(
    int Index,
    int TimeMs,
    int EndTimeMs,
    int LengthMs,
    int Type,
    string TypeName,
    bool IsAir,
    int MultiMaxHitCount,
    int MultiDurationMs)
{
    internal bool IsInside(IReadOnlyList<CsvNote> intervals)
    {
        foreach (var interval in intervals)
        {
            int end = interval.EndTimeMs > interval.TimeMs ? interval.EndTimeMs : interval.TimeMs + interval.MultiDurationMs;
            if (Type != 8 && TimeMs >= interval.TimeMs && TimeMs <= end)
                return true;
        }

        return false;
    }
}

internal static class CsvMapReader
{
    internal static CsvMap Read(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"CSV not found: {path}");

        string[] lines;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            lines = reader.ReadToEnd()
                .Split(["\r\n", "\n"], StringSplitOptions.None);
        }
        if (lines.Length < 2)
            return new CsvMap([], 0, 0);

        double metadataBpm = 0;
        double metadataRuntimeBpm = 0;
        int headerLineIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                ReadMetadata(line, ref metadataBpm, ref metadataRuntimeBpm);
                continue;
            }

            headerLineIndex = i;
            break;
        }

        if (headerLineIndex < 0)
            return new CsvMap([], metadataBpm, metadataRuntimeBpm);

        string[] headers = SplitCsvLine(lines[headerLineIndex]);
        var columns = headers
            .Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);

        int GetIndex(string name)
        {
            if (!columns.TryGetValue(name, out int index))
                throw new InvalidDataException($"Missing CSV column: {name}");
            return index;
        }

        int indexColumn = GetIndex("index");
        int timeMsColumn = GetIndex("time_ms");
        int endTimeMsColumn = GetIndex("end_time_ms");
        int lengthMsColumn = GetIndex("length_ms");
        int typeColumn = GetIndex("type");
        int typeNameColumn = GetIndex("type_name");
        int isAirColumn = GetIndex("is_air");
        int multiMaxColumn = GetIndex("multi_max_hit_count");
        int multiDurationColumn = GetIndex("multi_duration_ms");
        int bpmColumn = GetOptionalIndex("bpm");
        int runtimeBpmColumn = GetOptionalIndex("runtime_bpm");

        var notes = new List<CsvNote>(lines.Length - 1);
        for (int i = headerLineIndex + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
                continue;
            if (lines[i].TrimStart().StartsWith("#", StringComparison.Ordinal))
                continue;

            string[] cells = SplitCsvLine(lines[i]);
            if (metadataBpm <= 0 && bpmColumn >= 0)
                metadataBpm = ReadDouble(cells, bpmColumn);
            if (metadataRuntimeBpm <= 0 && runtimeBpmColumn >= 0)
                metadataRuntimeBpm = ReadDouble(cells, runtimeBpmColumn);

            notes.Add(new CsvNote(
                ReadInt(cells, indexColumn),
                ReadInt(cells, timeMsColumn),
                ReadInt(cells, endTimeMsColumn),
                ReadInt(cells, lengthMsColumn),
                ReadInt(cells, typeColumn),
                ReadString(cells, typeNameColumn),
                ReadBool(cells, isAirColumn),
                ReadInt(cells, multiMaxColumn),
                ReadInt(cells, multiDurationColumn)));
        }

        notes.Sort((a, b) =>
        {
            int timeCompare = a.TimeMs.CompareTo(b.TimeMs);
            return timeCompare != 0 ? timeCompare : a.Index.CompareTo(b.Index);
        });

        return new CsvMap(notes, metadataBpm, metadataRuntimeBpm);

        int GetOptionalIndex(string name)
        {
            return columns.TryGetValue(name, out int index) ? index : -1;
        }
    }

    private static void ReadMetadata(string line, ref double bpm, ref double runtimeBpm)
    {
        string body = line.TrimStart('#').Trim();
        foreach (string part in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0)
                continue;

            string key = part[..separator].Trim();
            string value = part[(separator + 1)..].Trim();
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                continue;

            if (key.Equals("bpm", StringComparison.OrdinalIgnoreCase))
                bpm = parsed;
            else if (key.Equals("runtime_bpm", StringComparison.OrdinalIgnoreCase))
                runtimeBpm = parsed;
        }
    }

    private static string[] SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (ch == ',' && !quoted)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        cells.Add(current.ToString());
        return cells.ToArray();
    }

    private static int ReadInt(string[] cells, int index)
    {
        if (index >= cells.Length || string.IsNullOrWhiteSpace(cells[index]))
            return 0;
        return int.Parse(cells[index], CultureInfo.InvariantCulture);
    }

    private static double ReadDouble(string[] cells, int index)
    {
        if (index < 0 || index >= cells.Length || string.IsNullOrWhiteSpace(cells[index]))
            return 0;
        return double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;
    }

    private static string ReadString(string[] cells, int index)
    {
        return index < cells.Length ? cells[index] : "";
    }

    private static bool ReadBool(string[] cells, int index)
    {
        return index < cells.Length && bool.TryParse(cells[index], out bool value) && value;
    }
}

internal enum Posture
{
    Ground,
    Air
}

internal enum ObjectReason
{
    Active,
    Hold,
    Boss,
    Multi,
    MusicCollect,
    BlockDodge
}

internal sealed record ManiaObject(int Lane, int StartMs, int EndMs, ObjectReason Reason)
{
    internal bool IsHold => EndMs > StartMs;
    internal Posture Posture => Lane is 1 or 2 or 5 ? Posture.Air : Posture.Ground;
}

internal sealed class BeatmapModel
{
    internal List<ManiaObject> Objects { get; } = [];
}

internal static class ManiaConverter
{
    private const int KeyCount = 6;
    private const int PreferredMinGapMs = 200;
    private const int BalanceWindowMs = 4000;
    private const int MusicWindowMs = 50;
    private const int BlockWindowMs = 120;
    // 有效滞空时长：起跳后这段时间内可以获取空中音符、规避地面齿轮（最后约 100ms 视为地面）。
    private const int AirHoldMs = 400;
    // 滞空动画总时长：期间不能二段跳。
    private const int AirborneAnimMs = 500;
    private const int MultiEndPaddingMs = 80;

    private static readonly int[] AirLanes = [1, 2, 5];
    private static readonly int[] GroundLanes = [3, 4, 6];
    private static readonly int[] AllLanes = [1, 2, 3, 4, 5, 6];

    // 阶段1的空地键描述：只含姿态与时间，不含具体轨道。轨道在阶段2分配。
    private readonly struct KeyDesc
    {
        internal readonly int StartMs;
        internal readonly int EndMs;
        internal readonly Posture Posture;
        internal readonly ObjectReason Reason;
        internal readonly int MultiSlot;

        internal KeyDesc(int startMs, int endMs, Posture posture, ObjectReason reason, int multiSlot = -1)
        {
            StartMs = startMs;
            EndMs = Math.Max(startMs, endMs);
            Posture = posture;
            Reason = reason;
            MultiSlot = multiSlot;
        }

        internal bool IsHold => EndMs > StartMs;
    }

    internal static BeatmapModel Convert(IReadOnlyList<CsvNote> notes, GeneratorOptions options)
    {
        var result = new BeatmapModel();

        // 阶段1：构建空地键集合（只定姿态，不分轨道）
        var keys = BuildKeyCollection(notes, options.Bpm);

        // 阶段2：根据集合分配轨道 + 换轨优化
        result.Objects.AddRange(AssignLanes(keys, options.Bpm));

        SortObjects(result.Objects);
        OptimizeShortGaps(result.Objects);
        NormalizeChordLanes(result.Objects, options.Bpm);
        OptimizeShortGaps(result.Objects);
        SortObjects(result.Objects);
        RemoveExactDuplicates(result.Objects);
        return result;
    }

    // ===== 阶段1：构建空地键集合（只定姿态，不分轨道） =====

    private static List<KeyDesc> BuildKeyCollection(IReadOnlyList<CsvNote> notes, double bpm)
    {
        var keys = new List<KeyDesc>();
        var multiNotes = notes.Where(n => n.Type == 8).ToList();

        foreach (var note in notes)
        {
            if (note.Type != 8 && note.IsInside(multiNotes))
                continue;

            switch (note.Type)
            {
                case 1:
                case 4:
                    keys.Add(new KeyDesc(note.TimeMs, note.TimeMs, note.IsAir ? Posture.Air : Posture.Ground, ObjectReason.Active));
                    break;
                case 3:
                    if (HasDuration(note))
                    {
                        int end = note.EndTimeMs > note.TimeMs ? note.EndTimeMs : note.TimeMs + Math.Max(0, note.LengthMs);
                        if (end <= note.TimeMs)
                            keys.Add(new KeyDesc(note.TimeMs, note.TimeMs, note.IsAir ? Posture.Air : Posture.Ground, ObjectReason.Active));
                        else
                            keys.Add(new KeyDesc(note.TimeMs, end, note.IsAir ? Posture.Air : Posture.Ground, ObjectReason.Hold));
                    }
                    break;
                case 5:
                    keys.Add(new KeyDesc(note.TimeMs, note.TimeMs, DecideBossPosture(keys, note.TimeMs), ObjectReason.Boss));
                    break;
                case 8:
                    AddMultiKeys(keys, note, bpm);
                    break;
            }
        }

        PlanGearLandings(keys, notes, multiNotes);

        foreach (var note in notes.Where(n => n.Type is 2 or 7).OrderBy(n => n.TimeMs).ThenBy(n => n.Index))
        {
            if (note.IsInside(multiNotes))
                continue;

            if (note.Type == 7)
                EnsureMusicCollected(keys, note);
            else
                EnsureBlockDodged(keys, note);
        }

        keys.Sort((a, b) =>
        {
            int timeCompare = a.StartMs.CompareTo(b.StartMs);
            return timeCompare != 0 ? timeCompare : a.Posture.CompareTo(b.Posture);
        });
        return keys;
    }

    private static bool HasDuration(CsvNote note)
    {
        return note.EndTimeMs > note.TimeMs || note.LengthMs > 0;
    }

    // 姿态模拟：只追踪姿态序列，不看具体轨道。
    private static Posture PostureAt(List<KeyDesc> keys, int timeMs)
    {
        int lastTime = int.MinValue;
        bool airAtLastTime = false;
        bool groundAtLastTime = false;

        foreach (var key in keys)
        {
            if (key.IsHold && key.StartMs <= timeMs && key.EndMs >= timeMs)
                return key.Posture;

            if (key.StartMs > timeMs)
                continue;

            if (key.StartMs > lastTime)
            {
                lastTime = key.StartMs;
                airAtLastTime = false;
                groundAtLastTime = false;
            }

            if (key.StartMs == lastTime)
            {
                if (key.Posture == Posture.Air)
                    airAtLastTime = true;
                else
                    groundAtLastTime = true;
            }
        }

        if (lastTime == int.MinValue)
            return Posture.Ground;
        if (groundAtLastTime)
            return Posture.Ground;
        if (airAtLastTime && timeMs <= lastTime + AirHoldMs)
            return Posture.Air;

        return Posture.Ground;
    }

    private static bool IsPostureSatisfied(List<KeyDesc> keys, int timeMs, Posture target, int windowMs)
    {
        return PostureAt(keys, timeMs - windowMs) == target
            || PostureAt(keys, timeMs) == target
            || PostureAt(keys, timeMs + windowMs) == target;
    }

    private static bool IsPostureUnsafe(List<KeyDesc> keys, int timeMs, Posture unsafePosture, int windowMs)
    {
        return PostureAt(keys, timeMs - windowMs) == unsafePosture
            || PostureAt(keys, timeMs) == unsafePosture
            || PostureAt(keys, timeMs + windowMs) == unsafePosture;
    }

    private static Posture DecideBossPosture(List<KeyDesc> keys, int timeMs)
    {
        int air = 0;
        int ground = 0;
        int begin = timeMs - BalanceWindowMs;
        foreach (var key in keys)
        {
            if (key.StartMs < begin || key.StartMs > timeMs)
                continue;

            if (key.Posture == Posture.Air)
                air++;
            else
                ground++;
        }

        return ground > air ? Posture.Air : Posture.Ground;
    }

    private static void AddMultiKeys(List<KeyDesc> keys, CsvNote note, double bpm)
    {
        int hitCount = Math.Max(1, note.MultiMaxHitCount);
        int end = note.EndTimeMs > note.TimeMs ? note.EndTimeMs : note.TimeMs + Math.Max(note.MultiDurationMs, 0);
        int available = Math.Max(0, end - note.TimeMs - MultiEndPaddingMs);
        if (available <= 0 || hitCount == 1)
        {
            keys.Add(new KeyDesc(note.TimeMs, note.TimeMs, Posture.Air, ObjectReason.Multi, 0));
            return;
        }

        int chordSize = ChooseMultiChordSize(hitCount, available);
        int slotCount = (int)Math.Ceiling(hitCount / (double)chordSize);
        double fallbackStep = slotCount <= 1 ? 0 : available / (double)(slotCount - 1);
        double bpmStep = ChooseBpmStepMs(bpm);
        double step = fallbackStep >= 100 && fallbackStep <= 125 ? fallbackStep : Math.Min(125, Math.Max(100, bpmStep));
        if (slotCount > 1 && step * (slotCount - 1) > available)
            step = available / (double)(slotCount - 1);

        int remaining = hitCount;
        for (int slot = 0; slot < slotCount && remaining > 0; slot++)
        {
            int time = note.TimeMs + (int)Math.Round(step * slot, MidpointRounding.AwayFromZero);
            time = Math.Min(time, end - MultiEndPaddingMs);
            int count = Math.Min(chordSize, remaining);
            for (int i = 0; i < count; i++)
            {
                // multi 姿态启发式：和弦内交替空/地（近似原始 MultiLanes 的混合姿态），具体轨道在阶段2分配。
                Posture posture = (slot + i) % 2 == 0 ? Posture.Ground : Posture.Air;
                keys.Add(new KeyDesc(time, time, posture, ObjectReason.Multi, slot));
            }
            remaining -= count;
        }
    }

    private static int ChooseMultiChordSize(int hitCount, int availableMs)
    {
        for (int chordSize = 1; chordSize <= KeyCount; chordSize++)
        {
            int slots = (int)Math.Ceiling(hitCount / (double)chordSize);
            if (slots <= 1 || availableMs / (double)(slots - 1) >= 100)
                return chordSize;
        }

        return KeyCount;
    }

    private static double ChooseBpmStepMs(double bpm)
    {
        double beatMs = 60000.0 / bpm;
        for (int div = 1; div <= 32; div *= 2)
        {
            double step = beatMs / div;
            if (step <= 125 && step >= 100)
                return step;
        }

        return 125;
    }

    private static int[] MultiLanes(int count, int slot)
    {
        bool left = slot % 2 == 0;
        return count switch
        {
            1 => [left ? 3 : 4],
            2 => left ? [2, 3] : [4, 5],
            3 => left ? [1, 2, 3] : [4, 5, 6],
            4 => left ? [1, 2, 3, 4] : [3, 4, 5, 6],
            5 => left ? [1, 2, 3, 4, 5] : [2, 3, 4, 5, 6],
            _ => [1, 2, 3, 4, 5, 6]
        };
    }

    private static void EnsureMusicCollected(List<KeyDesc> keys, CsvNote note)
    {
        Posture target = note.IsAir ? Posture.Air : Posture.Ground;
        if (IsPostureSatisfied(keys, note.TimeMs, target, MusicWindowMs))
            return;

        keys.Add(new KeyDesc(note.TimeMs, note.TimeMs, target, ObjectReason.MusicCollect));
    }

    private static void EnsureBlockDodged(List<KeyDesc> keys, CsvNote note)
    {
        Posture unsafePosture = note.IsAir ? Posture.Air : Posture.Ground;
        Posture safePosture = unsafePosture == Posture.Air ? Posture.Ground : Posture.Air;

        if (IsPostureSatisfied(keys, note.TimeMs, safePosture, BlockWindowMs))
            return;
        if (!IsPostureUnsafe(keys, note.TimeMs, unsafePosture, BlockWindowMs))
            return;

        keys.Add(new KeyDesc(note.TimeMs, note.TimeMs, safePosture, ObjectReason.BlockDodge));
    }

    // 齿轮落地修复（阶段1版）：只插入键（姿态），不分配轨道。
    private static void PlanGearLandings(
        List<KeyDesc> keys,
        IReadOnlyList<CsvNote> notes,
        IReadOnlyList<CsvNote> multiNotes)
    {
        const int RiskLowMs = 400;
        const int RiskHighMs = 600;

        // 地面点击的时刻，用于识别"天地双押"：同一时刻既有空中键又有地面键，结果是地面而非起跳。
        var groundTapMs = new HashSet<int>(keys
            .Where(k => !k.IsHold && k.Posture == Posture.Ground)
            .Select(k => k.StartMs));

        // 起跳事件 = 所有空中 tap，排除与地面键同刻的（天地双押）。
        var jumps = keys
            .Where(k => !k.IsHold && k.Posture == Posture.Air)
            .Where(k => !groundTapMs.Contains(k.StartMs))
            .Select(k => k.StartMs)
            .OrderBy(t => t)
            .ToList();
        int jumpIndex = 0;

        // "必须保持空中"的对象时刻（地面齿轮 + 空中 music），用于计算落地键下界 Tl
        var airRequirements = notes
            .Where(n => (n.Type == 2 && !n.IsAir) || (n.Type == 7 && n.IsAir))
            .Select(n => n.TimeMs)
            .OrderBy(t => t)
            .ToList();

        int currentJump = -1;

        foreach (var gear in notes.Where(n => n.Type == 2 && !n.IsAir).OrderBy(n => n.TimeMs).ThenBy(n => n.Index))
        {
            if (gear.IsInside(multiNotes))
                continue;

            int t = gear.TimeMs;

            // 只有"已落地"（距上次起跳 >= 动画时长）的空键才是真正的新起跳；
            // 滞空中的空键是 no-op（不能二段跳），不推进 currentJump。
            while (jumpIndex < jumps.Count && jumps[jumpIndex] <= t)
            {
                int j = jumps[jumpIndex];
                if (currentJump < 0 || j - currentJump >= AirborneAnimMs)
                    currentJump = j;
                jumpIndex++;
            }

            if (currentJump < 0)
            {
                // 首个齿轮：EnsureBlockDodged 会在 t 处插入空中键起跳，这里预判推进。
                currentJump = t;
                continue;
            }

            int gap = t - currentJump;
            if (gap < RiskLowMs)
                continue;
            if (gap > RiskHighMs)
            {
                // 已自然落地：EnsureBlockDodged 会在 t 处插入空中键起跳，预判推进。
                currentJump = t;
                continue;
            }

            // 危险窗口：最后一个"必须保持空中"的对象
            int tl = currentJump;
            foreach (int req in airRequirements)
            {
                if (req >= currentJump && req < t)
                    tl = req;
            }

            int tg = (tl + t) / 2;   // 提前落地（中点）
            int tj = t;              // 重新起跳 = block 同时按空，无提前量

            if (tj - tg < 1)
                tg = tj - 1;

            keys.Add(new KeyDesc(tg, tg, Posture.Ground, ObjectReason.BlockDodge));
            keys.Add(new KeyDesc(tj, tj, Posture.Air, ObjectReason.BlockDodge));

            currentJump = tj;
        }
    }

    // ===== 阶段2：根据空地键集合分配轨道 =====

    private static List<ManiaObject> AssignLanes(List<KeyDesc> keys, double bpm)
    {
        var objects = new List<ManiaObject>();
        var scheduler = new LaneScheduler(objects, bpm);

        foreach (var key in keys)
        {
            int lane = scheduler.ChooseLane(key.StartMs, key.EndMs, key.Posture, boss: false);
            scheduler.Add(lane, key.StartMs, key.EndMs, key.Reason);
        }

        return objects;
    }

    private static void RemoveExactDuplicates(List<ManiaObject> objects)
    {
        var seen = new HashSet<(int Lane, int Start, int End)>();
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            var key = (objects[i].Lane, objects[i].StartMs, objects[i].EndMs);
            if (!seen.Add(key))
                objects.RemoveAt(i);
        }
    }

    private static void OptimizeShortGaps(List<ManiaObject> objects)
    {
        for (int pass = 0; pass < 6; pass++)
        {
            bool changed = false;
            SortObjects(objects);

            for (int i = 0; i < objects.Count; i++)
            {
                var obj = objects[i];
                if (obj.Reason == ObjectReason.Multi)
                    continue;

                int previousEnd = LastEndBefore(objects, obj.Lane, obj.StartMs, i);
                int currentGap = obj.StartMs - previousEnd;
                if (currentGap >= PreferredMinGapMs)
                    continue;

                int bestLane = FindBetterLaneForGap(objects, i, currentGap);
                if (bestLane == 0)
                    continue;

                objects[i] = obj with { Lane = bestLane };
                changed = true;
            }

            if (!changed)
                break;
        }
    }

    private static int FindBetterLaneForGap(List<ManiaObject> objects, int objectIndex, int currentGap)
    {
        var obj = objects[objectIndex];
        int bestLane = 0;
        double bestScore = currentGap;

        foreach (int lane in CandidateLanesForObject(obj))
        {
            if (lane == obj.Lane)
                continue;
            if (objects.Any(o => o.Lane == lane && o.StartMs == obj.StartMs))
                continue;
            if (HasHoldOverlap(objects, lane, obj.StartMs, obj.EndMs, objectIndex))
                continue;

            int previousEnd = LastEndBefore(objects, lane, obj.StartMs, objectIndex);
            int nextStart = NextStartAfter(objects, lane, obj.EndMs, objectIndex);
            int beforeGap = obj.StartMs - previousEnd;
            int afterGap = nextStart - obj.EndMs;
            int minGap = Math.Min(beforeGap, afterGap);
            double score = minGap + BalanceScore(objects, lane, obj.StartMs);

            if (minGap > currentGap && score > bestScore)
            {
                bestScore = score;
                bestLane = lane;
            }
        }

        return bestLane;
    }

    private static int[] CandidateLanesForObject(ManiaObject obj)
    {
        if (obj.Reason == ObjectReason.Boss)
            return AllLanes;

        return obj.Posture == Posture.Air ? AirLanes : GroundLanes;
    }

    private static void NormalizeChordLanes(List<ManiaObject> objects, double bpm)
    {
        int minimumGapMs = (int)Math.Floor(60000.0 / Math.Max(1, bpm) / 2.0);
        for (int pass = 0; pass < 4; pass++)
        {
            bool changed = false;
            int[] times = objects.Select(o => o.StartMs).Distinct().Order().ToArray();
            foreach (int timeMs in times)
            {
                HashSet<int> lanes = objects
                    .Where(o => o.StartMs == timeMs)
                    .Select(o => o.Lane)
                    .ToHashSet();

                if (lanes.Contains(2) && lanes.Contains(4) && !lanes.Contains(3))
                {
                    changed |= TryMoveObjectLane(objects, timeMs, 4, 3, minimumGapMs)
                        || TryMoveObjectLane(objects, timeMs, 4, 6, minimumGapMs)
                        || TryMoveObjectLane(objects, timeMs, 2, 1, minimumGapMs)
                        || TryMoveObjectLane(objects, timeMs, 2, 5, minimumGapMs);
                }

                lanes = objects
                    .Where(o => o.StartMs == timeMs)
                    .Select(o => o.Lane)
                    .ToHashSet();

                if (lanes.Contains(3) && lanes.Contains(5) && !lanes.Contains(4) && !lanes.Contains(2))
                {
                    changed |= TryMoveObjectLane(objects, timeMs, 3, 4, minimumGapMs)
                        || TryMoveObjectLane(objects, timeMs, 3, 6, minimumGapMs)
                        || TryMoveObjectLane(objects, timeMs, 5, 1, minimumGapMs)
                        || TryMoveObjectLane(objects, timeMs, 5, 2, minimumGapMs);
                }
            }

            if (!changed)
                break;
        }
    }

    private static bool TryMoveObjectLane(List<ManiaObject> objects, int timeMs, int fromLane, int toLane, int minimumGapMs)
    {
        if (objects.Any(o => o.Lane == toLane && o.StartMs == timeMs))
            return false;

        int index = objects.FindIndex(o => o.StartMs == timeMs && o.Lane == fromLane);
        if (index >= 0)
        {
            var obj = objects[index];
            if (HasHoldOverlap(objects, toLane, obj.StartMs, obj.EndMs, index))
                return false;
            int previousGap = timeMs - LastEndBefore(objects, toLane, timeMs, index);
            int nextGap = NextStartAfter(objects, toLane, obj.EndMs, index) - obj.EndMs;
            if (Math.Min(previousGap, nextGap) <= minimumGapMs)
                return false;

            objects[index] = obj with { Lane = toLane };
            return true;
        }

        return false;
    }

    private static int LastEndBefore(List<ManiaObject> objects, int lane, int timeMs, int skipIndex = -1)
    {
        int lastEnd = 0;
        for (int i = 0; i < objects.Count; i++)
        {
            if (i == skipIndex)
                continue;

            var obj = objects[i];
            if (obj.Lane == lane && obj.EndMs <= timeMs)
                lastEnd = Math.Max(lastEnd, obj.EndMs);
        }

        return lastEnd;
    }

    private static int NextStartAfter(List<ManiaObject> objects, int lane, int timeMs, int skipIndex = -1)
    {
        int nextStart = int.MaxValue / 2;
        for (int i = 0; i < objects.Count; i++)
        {
            if (i == skipIndex)
                continue;

            var obj = objects[i];
            if (obj.Lane == lane && obj.StartMs >= timeMs)
                nextStart = Math.Min(nextStart, obj.StartMs);
        }

        return nextStart;
    }

    private static bool HasHoldOverlap(List<ManiaObject> objects, int lane, int startMs, int endMs, int skipIndex = -1)
    {
        for (int i = 0; i < objects.Count; i++)
        {
            if (i == skipIndex)
                continue;

            var obj = objects[i];
            if (obj.IsHold
                && obj.Lane == lane
                && obj.StartMs < Math.Max(startMs, endMs)
                && obj.EndMs > startMs)
                return true;
        }

        return false;
    }

    private static double BalanceScore(List<ManiaObject> objects, int lane, int startMs)
    {
        int left = 0;
        int right = 0;
        int begin = startMs - BalanceWindowMs;

        foreach (var obj in objects)
        {
            if (obj.StartMs < begin || obj.StartMs > startMs)
                continue;

            if (obj.Lane <= 3)
                left++;
            else
                right++;
        }

        if (left == right)
            return 0;

        bool laneIsLeft = lane <= 3;
        int diff = Math.Abs(left - right);
        return (left > right && !laneIsLeft) || (right > left && laneIsLeft)
            ? diff * 40.0
            : -diff * 40.0;
    }

    private static void SortObjects(List<ManiaObject> objects)
    {
        objects.Sort((a, b) =>
        {
            int timeCompare = a.StartMs.CompareTo(b.StartMs);
            return timeCompare != 0 ? timeCompare : a.Lane.CompareTo(b.Lane);
        });
    }

    private sealed class LaneScheduler
    {
        private readonly List<ManiaObject> _objects;
        private readonly int[] _laneEndMs = new int[KeyCount + 1];
        private readonly double _eighthNoteMs;

        internal LaneScheduler(List<ManiaObject> objects, double bpm)
        {
            _objects = objects;
            _eighthNoteMs = 60000.0 / Math.Max(1, bpm) / 2.0;
        }

        internal void Add(int lane, int startMs, int endMs, ObjectReason reason)
        {
            if (lane < 1 || lane > KeyCount)
                throw new ArgumentOutOfRangeException(nameof(lane), lane, "Invalid mania lane.");

            if (endMs < startMs)
                endMs = startMs;

            _objects.Add(new ManiaObject(lane, startMs, endMs, reason));
            _laneEndMs[lane] = Math.Max(_laneEndMs[lane], endMs);
        }

        internal int ChooseLane(int startMs, int endMs, Posture posture, bool boss)
        {
            int? chordLane = TryChooseChordPartnerLane(startMs, endMs, posture, boss);
            if (chordLane.HasValue)
                return chordLane.Value;

            return BestLane(AllowedLanes(posture, boss), startMs, endMs);
        }

        private int? TryChooseChordPartnerLane(int startMs, int endMs, Posture posture, bool boss)
        {
            HashSet<int> sameTimeLanes = _objects
                .Where(o => o.StartMs == startMs)
                .Select(o => o.Lane)
                .ToHashSet();

            if (sameTimeLanes.Count == 0)
                return null;

            int[] candidates = ChordPartnerCandidates(sameTimeLanes, posture, boss);
            foreach (int lane in candidates)
            {
                if (!AllowedLanes(posture, boss).Contains(lane))
                    continue;

                if (IsLaneUsable(lane, startMs, endMs, sameTimeLanes) && LaneGap(lane, startMs) >= _eighthNoteMs)
                    return lane;

                if (TryMovePreviousObjectForLane(lane, startMs, endMs, posture, boss, sameTimeLanes))
                    return lane;
            }

            return null;
        }

        private static int[] ChordPartnerCandidates(HashSet<int> lanes, Posture posture, bool boss)
        {
            if (boss)
            {
                if (lanes.Contains(2))
                    return [3, 4, 5, 1, 6];
                if (lanes.Contains(5))
                    return [4, 3, 2, 6, 1];
                if (lanes.Contains(3))
                    return [2, 1, 5, 4, 6];
                if (lanes.Contains(4))
                    return [5, 6, 2, 3, 1];
                return [];
            }

            if (posture == Posture.Air)
            {
                if (lanes.Contains(2) && lanes.Contains(3))
                    return [1, 5];
                if (lanes.Contains(1) && lanes.Contains(3))
                    return [2, 5];
                if (lanes.Contains(3))
                    return [2, 1];
                if (lanes.Contains(4) || lanes.Contains(6))
                    return [5];
            }
            else
            {
                if (lanes.Contains(1) && lanes.Contains(2))
                    return [3, 4, 6];
                if (lanes.Contains(4) && lanes.Contains(5))
                    return [6, 3];
                if (lanes.Contains(2) || lanes.Contains(1))
                    return [3];
                if (lanes.Contains(5))
                    return [4, 6];
                if (lanes.Contains(4))
                    return [6];
            }

            return [];
        }

        private bool IsLaneUsable(int lane, int startMs, int endMs, HashSet<int> sameTimeLanes)
        {
            return lane >= 1
                && lane <= KeyCount
                && !sameTimeLanes.Contains(lane)
                && !HasHoldOverlap(lane, startMs, endMs);
        }

        private bool TryMovePreviousObjectForLane(int lane, int startMs, int endMs, Posture posture, bool boss, HashSet<int> sameTimeLanes)
        {
            if (!IsLaneUsable(lane, startMs, endMs, sameTimeLanes))
                return false;

            var previous = _objects
                .Select((obj, index) => (obj, index))
                .Where(x => x.obj.Lane == lane && x.obj.StartMs < startMs && x.obj.EndMs <= startMs)
                .OrderByDescending(x => x.obj.EndMs)
                .FirstOrDefault();

            if (previous.obj == null || previous.obj.IsHold || previous.obj.Reason == ObjectReason.Multi)
                return false;

            double currentGapWithoutMove = LaneGap(lane, startMs);
            if (currentGapWithoutMove >= _eighthNoteMs)
                return true;

            int previousLane = previous.obj.Lane;
            int previousStart = previous.obj.StartMs;
            int previousEnd = previous.obj.EndMs;
            int bestLane = 0;
            double bestScore = double.NegativeInfinity;

            foreach (int targetLane in AllowedLanes(previous.obj.Posture, previous.obj.Reason == ObjectReason.Boss))
            {
                if (targetLane == previousLane)
                    continue;
                if (HasObjectAt(targetLane, previousStart))
                    continue;
                if (HasHoldOverlap(targetLane, previousStart, previousEnd))
                    continue;

                double beforeGap = previousStart - LaneEndBefore(targetLane, previousStart);
                double afterGap = LaneStartAfter(targetLane, previousEnd) - previousEnd;
                double minGap = Math.Min(beforeGap, afterGap);
                double balanceScore = BalanceScore(targetLane, previousStart);
                double score = minGap + balanceScore;
                if (minGap >= _eighthNoteMs && score > bestScore)
                {
                    bestScore = score;
                    bestLane = targetLane;
                }
            }

            if (bestLane == 0)
                return false;

            int preferredLaneGapAfterMove = startMs - LaneEndBefore(lane, startMs, previous.index);
            if (preferredLaneGapAfterMove <= _eighthNoteMs)
                return false;

            _objects[previous.index] = previous.obj with { Lane = bestLane };
            RebuildLaneEnds();
            return true;
        }

        internal bool IsPostureSatisfied(int timeMs, Posture target, int windowMs)
        {
            for (int t = timeMs - windowMs; t <= timeMs + windowMs; t += Math.Max(1, windowMs))
            {
                if (PostureAt(t) == target)
                    return true;
            }

            return PostureAt(timeMs) == target;
        }

        internal bool IsPostureUnsafe(int timeMs, Posture unsafePosture, int windowMs)
        {
            for (int t = timeMs - windowMs; t <= timeMs + windowMs; t += Math.Max(1, windowMs / 2))
            {
                if (PostureAt(t) == unsafePosture)
                    return true;
            }

            return false;
        }

        private int BestLane(int[] lanes, int startMs, int endMs)
        {
            int bestLane = 0;
            double bestScore = double.NegativeInfinity;
            foreach (int lane in lanes)
            {
                if (HasObjectAt(lane, startMs))
                    continue;
                if (HasHoldOverlap(lane, startMs, endMs))
                    continue;

                int endGap = startMs - LaneEndBefore(lane, startMs);
                double shortGapPenalty = endGap < PreferredMinGapMs
                    ? (PreferredMinGapMs - endGap) * 20.0
                    : 0;
                double overlapPenalty = endGap < 0 ? 1_000_000 + Math.Abs(endGap) : 0;
                double score = endGap - shortGapPenalty - overlapPenalty + BalanceScore(lane, startMs);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestLane = lane;
                }
            }

            if (bestLane != 0)
                return bestLane;

            return lanes.FirstOrDefault(lane => !HasHoldOverlap(lane, startMs, endMs), lanes[0]);
        }

        private static int[] AllowedLanes(Posture posture, bool boss)
        {
            if (boss)
                return AllLanes;
            return posture == Posture.Air ? AirLanes : GroundLanes;
        }

        private double BalanceScore(int lane, int startMs)
        {
            int left = 0;
            int right = 0;
            int begin = startMs - BalanceWindowMs;
            foreach (var obj in _objects)
            {
                if (obj.StartMs < begin || obj.StartMs > startMs)
                    continue;

                if (obj.Lane <= 3)
                    left++;
                else
                    right++;
            }

            if (left == right)
                return 0;

            bool laneIsLeft = lane <= 3;
            int diff = Math.Abs(left - right);
            return (left > right && !laneIsLeft) || (right > left && laneIsLeft)
                ? diff * 80.0
                : -diff * 80.0;
        }

        private bool HasObjectAt(int lane, int timeMs)
        {
            return _objects.Any(o => o.Lane == lane && o.StartMs == timeMs);
        }

        private bool HasHoldOverlap(int lane, int startMs, int endMs)
        {
            return _objects.Any(o => o.IsHold
                && o.Lane == lane
                && o.StartMs < Math.Max(startMs, endMs)
                && o.EndMs > startMs);
        }

        private double LaneGap(int lane, int startMs)
        {
            return startMs - LaneEndBefore(lane, startMs);
        }

        private int LaneEndBefore(int lane, int timeMs, int skipIndex = -1)
        {
            int lastEnd = 0;
            for (int i = 0; i < _objects.Count; i++)
            {
                if (i == skipIndex)
                    continue;

                var obj = _objects[i];
                if (obj.Lane == lane && obj.EndMs <= timeMs)
                    lastEnd = Math.Max(lastEnd, obj.EndMs);
            }

            return lastEnd;
        }

        private int LaneStartAfter(int lane, int timeMs)
        {
            int nextStart = int.MaxValue / 2;
            foreach (var obj in _objects)
            {
                if (obj.Lane == lane && obj.StartMs >= timeMs)
                    nextStart = Math.Min(nextStart, obj.StartMs);
            }

            return nextStart;
        }

        private void RebuildLaneEnds()
        {
            Array.Clear(_laneEndMs, 0, _laneEndMs.Length);
            foreach (var obj in _objects)
                _laneEndMs[obj.Lane] = Math.Max(_laneEndMs[obj.Lane], obj.EndMs);
        }

        private Posture PostureAt(int timeMs)
        {
            Posture posture = Posture.Ground;
            int lastTime = int.MinValue;
            bool airAtLastTime = false;
            bool groundAtLastTime = false;

            foreach (var obj in _objects)
            {
                if (obj.IsHold && obj.StartMs <= timeMs && obj.EndMs >= timeMs)
                    return obj.Posture;

                if (obj.StartMs > timeMs)
                    continue;

                if (obj.StartMs > lastTime)
                {
                    lastTime = obj.StartMs;
                    airAtLastTime = false;
                    groundAtLastTime = false;
                }

                if (obj.StartMs == lastTime)
                {
                    if (obj.Posture == Posture.Air)
                        airAtLastTime = true;
                    else
                        groundAtLastTime = true;
                }
            }

            if (lastTime == int.MinValue)
                return Posture.Ground;
            if (groundAtLastTime)
                return Posture.Ground;
            if (airAtLastTime && timeMs <= lastTime + AirHoldMs)
                return Posture.Air;

            return posture;
        }
    }
}

internal static class OsuWriter
{
    internal static void Write(BeatmapModel beatmap, GeneratorOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath)) ?? ".");

        var sb = new StringBuilder();
        sb.AppendLine("osu file format v14");
        sb.AppendLine();
        sb.AppendLine("[General]");
        sb.AppendLine($"AudioFilename: {options.AudioFileName}");
        sb.AppendLine("AudioLeadIn: 0");
        sb.AppendLine("PreviewTime: -1");
        sb.AppendLine("Countdown: 0");
        sb.AppendLine("SampleSet: Normal");
        sb.AppendLine("StackLeniency: 0.7");
        sb.AppendLine("Mode: 3");
        sb.AppendLine("LetterboxInBreaks: 0");
        sb.AppendLine("SpecialStyle: 0");
        sb.AppendLine("WidescreenStoryboard: 0");
        sb.AppendLine();
        sb.AppendLine("[Editor]");
        sb.AppendLine("DistanceSpacing: 1");
        sb.AppendLine("BeatDivisor: 4");
        sb.AppendLine("GridSize: 4");
        sb.AppendLine("TimelineZoom: 1");
        sb.AppendLine();
        sb.AppendLine("[Metadata]");
        sb.AppendLine($"Title:{options.Title}");
        sb.AppendLine($"TitleUnicode:{options.Title}");
        sb.AppendLine($"Artist:{options.Artist}");
        sb.AppendLine($"ArtistUnicode:{options.Artist}");
        sb.AppendLine($"Creator:{options.Creator}");
        sb.AppendLine($"Version:{options.Version}");
        sb.AppendLine("Source:Muse Dash");
        sb.AppendLine("Tags:ManiaInMuse converted");
        sb.AppendLine("BeatmapID:0");
        sb.AppendLine("BeatmapSetID:-1");
        sb.AppendLine();
        sb.AppendLine("[Difficulty]");
        sb.AppendLine("HPDrainRate:5");
        sb.AppendLine("CircleSize:6");
        sb.AppendLine("OverallDifficulty:8");
        sb.AppendLine("ApproachRate:5");
        sb.AppendLine("SliderMultiplier:1.4");
        sb.AppendLine("SliderTickRate:1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("//Background and Video events");
        sb.AppendLine("//Break Periods");
        sb.AppendLine("//Storyboard Layer 0 (Background)");
        sb.AppendLine("//Storyboard Layer 1 (Fail)");
        sb.AppendLine("//Storyboard Layer 2 (Pass)");
        sb.AppendLine("//Storyboard Layer 3 (Foreground)");
        sb.AppendLine("//Storyboard Sound Samples");
        sb.AppendLine();
        sb.AppendLine("[TimingPoints]");
        double beatLength = 60000.0 / options.Bpm;
        sb.AppendLine($"0,{beatLength.ToString("0.############", CultureInfo.InvariantCulture)},4,2,1,60,1,0");
        sb.AppendLine();
        sb.AppendLine("[HitObjects]");

        foreach (var obj in beatmap.Objects)
        {
            int x = LaneToX(obj.Lane);
            if (obj.IsHold)
                sb.AppendLine($"{x},192,{obj.StartMs},128,0,{obj.EndMs}:0:0:0:0:");
            else
                sb.AppendLine($"{x},192,{obj.StartMs},1,0,0:0:0:0:");
        }

        File.WriteAllText(options.OutputPath, sb.ToString(), new UTF8Encoding(false));
    }

    private static int LaneToX(int lane)
    {
        return (int)Math.Floor((lane - 0.5) * 512 / 6.0);
    }
}
