using System.Globalization;
using System.Text;

namespace AccuracyIndicator;

internal static class RuntimeOsuMapBuilder
{
    private const int KeyCount = 6;
    private const float PreferredMinGapSec = 0.2f;
    private const float BalanceWindowSec = 4f;
    private const float AirHoldSec = 0.5f;
    private const float MusicWindowSec = 0.05f;
    private const float BlockWindowSec = 0.12f;
    private const float MultiEndPaddingSec = 0.08f;

    private static readonly int[] AirLanes = [1, 2, 5];
    private static readonly int[] GroundLanes = [3, 4, 6];
    private static readonly int[] AllLanes = [1, 2, 3, 4, 5, 6];

    internal static IReadOnlyList<OsuPlayObject> Build(IReadOnlyList<NoteInfo> notes, float bpm)
    {
        var objects = new List<OsuPlayObject>();
        var scheduler = new RuntimeLaneScheduler(objects);
        var multiNotes = notes.Where(n => n.Type == 8).ToList();

        foreach (var note in notes.OrderBy(n => n.TimeSec))
        {
            if (note.Type != 8 && IsInsideMulti(note, multiNotes))
                continue;

            switch (note.Type)
            {
                case 1:
                case 4:
                    AddTap(note.TimeSec, note.IsAir ? RuntimePosture.Air : RuntimePosture.Ground, scheduler, boss: false);
                    break;
                case 3:
                    if (note.EndTimeSec > note.TimeSec)
                        AddHold(note.TimeSec, note.EndTimeSec, note.IsAir ? RuntimePosture.Air : RuntimePosture.Ground, scheduler);
                    break;
                case 5:
                    AddTap(note.TimeSec, RuntimePosture.Ground, scheduler, boss: true);
                    break;
                case 8:
                    AddMulti(note, scheduler, bpm);
                    break;
            }
        }

        foreach (var note in notes.Where(n => n.Type is 2 or 7).OrderBy(n => n.TimeSec))
        {
            if (IsInsideMulti(note, multiNotes))
                continue;

            if (note.Type == 7)
                EnsureMusicCollected(note, scheduler);
            else
                EnsureBlockDodged(note, scheduler);
        }

        objects.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        RemoveExactDuplicates(objects);
        return objects;
    }

    private static bool IsInsideMulti(NoteInfo note, IReadOnlyList<NoteInfo> multiNotes)
    {
        foreach (var multi in multiNotes)
        {
            float end = multi.EndTimeSec > multi.TimeSec ? multi.EndTimeSec : multi.TimeSec + multi.MultiDurationSec;
            if (note.Type != 8 && note.TimeSec >= multi.TimeSec && note.TimeSec <= end)
                return true;
        }

        return false;
    }

    private static void AddTap(float timeSec, RuntimePosture posture, RuntimeLaneScheduler scheduler, bool boss)
    {
        int lane = scheduler.ChooseLane(timeSec, timeSec, posture, boss);
        scheduler.Add(lane, timeSec, timeSec);
    }

    private static void AddHold(float startSec, float endSec, RuntimePosture posture, RuntimeLaneScheduler scheduler)
    {
        int lane = scheduler.ChooseLane(startSec, endSec, posture, boss: false);
        scheduler.Add(lane, startSec, endSec);
    }

    private static void AddMulti(NoteInfo note, RuntimeLaneScheduler scheduler, float bpm)
    {
        int hitCount = Math.Max(1, note.MultiMaxHitCount);
        float endSec = note.EndTimeSec > note.TimeSec ? note.EndTimeSec : note.TimeSec + Math.Max(note.MultiDurationSec, 0);
        float available = Math.Max(0, endSec - note.TimeSec - MultiEndPaddingSec);
        if (available <= 0 || hitCount == 1)
        {
            scheduler.Add(3, note.TimeSec, note.TimeSec);
            return;
        }

        int chordSize = ChooseMultiChordSize(hitCount, available);
        int slotCount = (int)Math.Ceiling(hitCount / (double)chordSize);
        double fallbackStep = slotCount <= 1 ? 0 : available / (double)(slotCount - 1);
        double bpmStep = ChooseBpmStepSec(bpm);
        double step = fallbackStep >= 0.1 && fallbackStep <= 0.125 ? fallbackStep : Math.Min(0.125, Math.Max(0.1, bpmStep));
        if (slotCount > 1 && step * (slotCount - 1) > available)
            step = available / (double)(slotCount - 1);

        int remaining = hitCount;
        for (int slot = 0; slot < slotCount && remaining > 0; slot++)
        {
            float time = note.TimeSec + (float)(step * slot);
            time = Math.Min(time, endSec - MultiEndPaddingSec);
            int count = Math.Min(chordSize, remaining);
            foreach (int lane in MultiLanes(count, slot))
                scheduler.Add(lane, time, time);
            remaining -= count;
        }
    }

    private static int ChooseMultiChordSize(int hitCount, float availableSec)
    {
        for (int chordSize = 1; chordSize <= KeyCount; chordSize++)
        {
            int slots = (int)Math.Ceiling(hitCount / (double)chordSize);
            if (slots <= 1 || availableSec / (slots - 1) >= 0.1)
                return chordSize;
        }

        return KeyCount;
    }

    private static double ChooseBpmStepSec(float bpm)
    {
        double beatSec = 60.0 / Math.Max(1, bpm);
        for (int div = 1; div <= 32; div *= 2)
        {
            double step = beatSec / div;
            if (step <= 0.125 && step >= 0.1)
                return step;
        }

        return 0.125;
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

    private static void EnsureMusicCollected(NoteInfo note, RuntimeLaneScheduler scheduler)
    {
        RuntimePosture target = note.IsAir ? RuntimePosture.Air : RuntimePosture.Ground;
        if (scheduler.IsPostureSatisfied(note.TimeSec, target, MusicWindowSec))
            return;

        AddTap(note.TimeSec, target, scheduler, boss: false);
    }

    private static void EnsureBlockDodged(NoteInfo note, RuntimeLaneScheduler scheduler)
    {
        RuntimePosture unsafePosture = note.IsAir ? RuntimePosture.Air : RuntimePosture.Ground;
        RuntimePosture safePosture = unsafePosture == RuntimePosture.Air ? RuntimePosture.Ground : RuntimePosture.Air;

        if (scheduler.IsPostureSatisfied(note.TimeSec, safePosture, BlockWindowSec))
            return;
        if (!scheduler.IsPostureUnsafe(note.TimeSec, unsafePosture, BlockWindowSec))
            return;

        AddTap(note.TimeSec, safePosture, scheduler, boss: false);
    }

    private static void RemoveExactDuplicates(List<OsuPlayObject> objects)
    {
        var seen = new HashSet<(int Lane, float Start, float End)>();
        for (int i = objects.Count - 1; i >= 0; i--)
        {
            var obj = objects[i];
            if (!seen.Add((obj.Lane, obj.StartSec, obj.EndSec)))
                objects.RemoveAt(i);
        }
    }

    private enum RuntimePosture
    {
        Ground,
        Air
    }

    private sealed class RuntimeLaneScheduler
    {
        private readonly List<OsuPlayObject> _objects;

        internal RuntimeLaneScheduler(List<OsuPlayObject> objects)
        {
            _objects = objects;
        }

        internal void Add(int lane, float startSec, float endSec)
        {
            _objects.Add(new OsuPlayObject(lane, startSec, Math.Max(startSec, endSec), endSec > startSec));
        }

        internal int ChooseLane(float startSec, float endSec, RuntimePosture posture, bool boss)
        {
            int[] lanes = boss ? AllLanes : posture == RuntimePosture.Air ? AirLanes : GroundLanes;
            int bestLane = lanes[0];
            double bestScore = double.NegativeInfinity;

            foreach (int lane in lanes)
            {
                if (HasObjectAt(lane, startSec) || HasHoldOverlap(lane, startSec, endSec))
                    continue;

                float previousEnd = LastEndBefore(lane, startSec);
                float gap = startSec - previousEnd;
                double shortGapPenalty = gap < PreferredMinGapSec ? (PreferredMinGapSec - gap) * 20.0 : 0;
                double score = gap - shortGapPenalty + BalanceScore(lane, startSec);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestLane = lane;
                }
            }

            return bestLane;
        }

        internal bool IsPostureSatisfied(float timeSec, RuntimePosture target, float windowSec)
        {
            return PostureAt(timeSec - windowSec) == target
                || PostureAt(timeSec) == target
                || PostureAt(timeSec + windowSec) == target;
        }

        internal bool IsPostureUnsafe(float timeSec, RuntimePosture unsafePosture, float windowSec)
        {
            return PostureAt(timeSec - windowSec) == unsafePosture
                || PostureAt(timeSec) == unsafePosture
                || PostureAt(timeSec + windowSec) == unsafePosture;
        }

        private bool HasObjectAt(int lane, float timeSec)
        {
            return _objects.Any(o => o.Lane == lane && Math.Abs(o.StartSec - timeSec) < 0.0005f);
        }

        private bool HasHoldOverlap(int lane, float startSec, float endSec)
        {
            return _objects.Any(o => o.IsHold
                && o.Lane == lane
                && o.StartSec < Math.Max(startSec, endSec)
                && o.EndSec > startSec);
        }

        private float LastEndBefore(int lane, float timeSec)
        {
            float lastEnd = 0;
            foreach (var obj in _objects)
            {
                if (obj.Lane == lane && obj.EndSec <= timeSec)
                    lastEnd = Math.Max(lastEnd, obj.EndSec);
            }

            return lastEnd;
        }

        private double BalanceScore(int lane, float startSec)
        {
            int left = 0;
            int right = 0;
            float begin = startSec - BalanceWindowSec;
            foreach (var obj in _objects)
            {
                if (obj.StartSec < begin || obj.StartSec > startSec)
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
                ? diff * 0.08
                : -diff * 0.08;
        }

        private RuntimePosture PostureAt(float timeSec)
        {
            int lastTimeCompare = int.MinValue;
            float lastTime = float.MinValue;
            bool airAtLastTime = false;
            bool groundAtLastTime = false;

            foreach (var obj in _objects)
            {
                if (obj.IsHold && obj.StartSec <= timeSec && obj.EndSec >= timeSec)
                    return IsAirLane(obj.Lane) ? RuntimePosture.Air : RuntimePosture.Ground;

                if (obj.StartSec > timeSec)
                    continue;

                int timeCompare = (int)Math.Round(obj.StartSec * 1000f);
                if (timeCompare > lastTimeCompare)
                {
                    lastTimeCompare = timeCompare;
                    lastTime = obj.StartSec;
                    airAtLastTime = false;
                    groundAtLastTime = false;
                }

                if (timeCompare == lastTimeCompare)
                {
                    if (IsAirLane(obj.Lane))
                        airAtLastTime = true;
                    else
                        groundAtLastTime = true;
                }
            }

            if (lastTimeCompare == int.MinValue)
                return RuntimePosture.Ground;
            if (groundAtLastTime)
                return RuntimePosture.Ground;
            if (airAtLastTime && timeSec <= lastTime + AirHoldSec)
                return RuntimePosture.Air;

            return RuntimePosture.Ground;
        }

        private static bool IsAirLane(int lane)
        {
            return lane is 1 or 2 or 5;
        }
    }
}

internal static class RuntimeOsuWriter
{
    private const string ExportDirectory = "UserData\\ManiaInMuse\\maps";

    internal static void SaveLatest(IReadOnlyList<OsuPlayObject> objects, float bpm)
    {
        Directory.CreateDirectory(ExportDirectory);
        string path = Path.Combine(ExportDirectory, "latest.osu");
        var sb = new StringBuilder();
        sb.AppendLine("osu file format v14");
        sb.AppendLine();
        sb.AppendLine("[General]");
        sb.AppendLine("AudioFilename: audio.mp3");
        sb.AppendLine("Mode: 3");
        sb.AppendLine();
        sb.AppendLine("[Metadata]");
        sb.AppendLine("Title:ManiaInMuse Runtime");
        sb.AppendLine("Artist:PeroPeroGames");
        sb.AppendLine("Creator:ManiaInMuse");
        sb.AppendLine("Version:Runtime");
        sb.AppendLine();
        sb.AppendLine("[Difficulty]");
        sb.AppendLine("CircleSize:6");
        sb.AppendLine("OverallDifficulty:8");
        sb.AppendLine();
        sb.AppendLine("[TimingPoints]");
        float beatLength = 60000f / Math.Max(1, bpm);
        sb.AppendLine($"0,{beatLength.ToString("0.############", CultureInfo.InvariantCulture)},4,2,1,60,1,0");
        sb.AppendLine();
        sb.AppendLine("[HitObjects]");

        foreach (var obj in objects.OrderBy(o => o.StartSec).ThenBy(o => o.Lane))
        {
            int x = LaneToX(obj.Lane);
            int startMs = ToMs(obj.StartSec);
            if (obj.IsHold)
                sb.AppendLine($"{x},192,{startMs},128,0,{ToMs(obj.EndSec)}:0:0:0:0:");
            else
                sb.AppendLine($"{x},192,{startMs},1,0,0:0:0:0:");
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        MelonLogger.Msg($"[ManiaTest] Runtime osu exported: {path} ({objects.Count} objects)");
    }

    private static int LaneToX(int lane)
    {
        return (int)Math.Floor((lane - 0.5) * 512 / 6.0);
    }

    private static int ToMs(float seconds)
    {
        return (int)Math.Round(seconds * 1000f, MidpointRounding.AwayFromZero);
    }
}
