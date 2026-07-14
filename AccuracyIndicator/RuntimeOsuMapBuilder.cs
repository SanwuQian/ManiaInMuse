using System.Globalization;
using System.Text;

namespace AccuracyIndicator;

internal static class RuntimeOsuMapBuilder
{
    private const float PreferredMinGapSec = 0.2f;
    private const float BalanceWindowSec = 4f;
    private const float AirHoldSec = 0.5f;
    private const float MusicWindowSec = 0.05f;
    private const float BlockWindowSec = 0.12f;
    private const float MultiEndPaddingSec = 0.08f;

    internal static IReadOnlyList<OsuPlayObject> Build(IReadOnlyList<NoteInfo> notes, float bpm, PlayerConfig config)
    {
        var objects = new List<OsuPlayObject>();
        var scheduler = new RuntimeLaneScheduler(objects, config);
        var multiNotes = notes.Where(n => n.Type == 8).ToList();

        foreach (var note in notes.OrderBy(n => n.TimeSec))
        {
            if (note.Type != 8 && IsInsideMulti(note, multiNotes))
                continue;

            switch (note.Type)
            {
                case 1:
                case 4:
                    AddTap(note.TimeSec, note.IsAir ? LanePosture.Air : LanePosture.Ground, scheduler, boss: false, OsuPlayObjectKind.RegularTap);
                    break;
                case 3:
                    if (note.EndTimeSec > note.TimeSec)
                        AddHold(note.TimeSec, note.EndTimeSec, note.IsAir ? LanePosture.Air : LanePosture.Ground, scheduler);
                    break;
                case 5:
                    AddTap(note.TimeSec, LanePosture.Ground, scheduler, boss: true, OsuPlayObjectKind.BossTap);
                    break;
                case 8:
                    AddMulti(note, scheduler, bpm);
                    break;
            }
        }

        if (config.EnableLocalSwapOptimizer)
            LocalSwapOptimizer.Optimize(objects, config);

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

    private static void AddTap(float timeSec, LanePosture posture, RuntimeLaneScheduler scheduler, bool boss, OsuPlayObjectKind kind)
    {
        int lane = scheduler.ChooseLane(timeSec, timeSec, posture, boss);
        scheduler.Add(lane, timeSec, timeSec, kind);
    }

    private static void AddHold(float startSec, float endSec, LanePosture posture, RuntimeLaneScheduler scheduler)
    {
        int lane = scheduler.ChooseLane(startSec, endSec, posture, boss: false);
        scheduler.Add(lane, startSec, endSec, OsuPlayObjectKind.Hold);
    }

    private static void AddMulti(NoteInfo note, RuntimeLaneScheduler scheduler, float bpm)
    {
        int hitCount = Math.Max(1, note.MultiMaxHitCount);
        float endSec = note.EndTimeSec > note.TimeSec ? note.EndTimeSec : note.TimeSec + Math.Max(note.MultiDurationSec, 0);
        float available = Math.Max(0, endSec - note.TimeSec - MultiEndPaddingSec);
        if (available <= 0 || hitCount == 1)
        {
            foreach (int lane in scheduler.ChooseMultiLanes(note.TimeSec, 1, 0))
                scheduler.Add(lane, note.TimeSec, note.TimeSec, OsuPlayObjectKind.Multi);
            return;
        }

        var pattern = scheduler.ChooseMultiPattern(note.TimeSec, endSec, hitCount, available);
        if (!pattern.IsUsable)
        {
            MelonLogger.Warning($"[ManiaInMuse] Multi at {note.TimeSec:F3}s could not build a stable left/right pattern; falling back to free lanes");
            AddFallbackMulti(note, scheduler, bpm, hitCount, endSec, available);
            return;
        }

        int slotCount = pattern.SlotsNeeded(hitCount);
        double fallbackStep = slotCount <= 1 ? 0 : available / (double)(slotCount - 1);
        double bpmStep = ChooseBpmStepSec(bpm);
        double step = fallbackStep >= 0.1 && fallbackStep <= 0.125 ? fallbackStep : Math.Min(0.125, Math.Max(0.1, bpmStep));
        if (slotCount > 1 && step * (slotCount - 1) > available)
            step = available / (double)(slotCount - 1);

        int remaining = hitCount;
        int slot = 0;
        float latestTime = endSec - MultiEndPaddingSec;
        while (remaining > 0)
        {
            float time = note.TimeSec + (float)(step * slot);
            if (time > latestTime + 0.0005f)
                break;

            int[] lanes = pattern.LanesForSlot(slot, remaining);
            if (!scheduler.AreLanesFreeAt(lanes, time))
            {
                MelonLogger.Warning($"[ManiaInMuse] Multi at {note.TimeSec:F3}s pattern lane occupied at {time:F3}s; falling back to free lanes");
                AddFallbackMulti(note, scheduler, bpm, remaining, endSec, Math.Max(0, latestTime - time), time, slot);
                return;
            }

            foreach (int lane in lanes)
                scheduler.Add(lane, time, time, OsuPlayObjectKind.Multi);

            remaining -= lanes.Length;
            slot++;
        }

        if (remaining > 0)
            MelonLogger.Warning($"[ManiaInMuse] Multi at {note.TimeSec:F3}s could not place {remaining}/{hitCount} hits without lane overlap");
    }

    private static void AddFallbackMulti(NoteInfo note, RuntimeLaneScheduler scheduler, float bpm, int hitCount, float endSec, float available, float startSec = -1, int firstSlot = 0)
    {
        int chordSize = ChooseMultiChordSize(hitCount, available, scheduler.LaneCount);
        int slotCount = Math.Max(1, (int)Math.Ceiling(hitCount / (double)chordSize));
        double fallbackStep = slotCount <= 1 ? 0 : available / (double)(slotCount - 1);
        double bpmStep = ChooseBpmStepSec(bpm);
        double step = fallbackStep >= 0.1 && fallbackStep <= 0.125 ? fallbackStep : Math.Min(0.125, Math.Max(0.1, bpmStep));
        if (slotCount > 1 && step * (slotCount - 1) > available)
            step = available / (double)(slotCount - 1);

        int remaining = hitCount;
        int slot = firstSlot;
        float baseTime = startSec >= 0 ? startSec : note.TimeSec;
        float latestTime = endSec - MultiEndPaddingSec;
        while (remaining > 0)
        {
            float time = baseTime + (float)(step * (slot - firstSlot));
            if (time > latestTime + 0.0005f)
                break;

            int[] lanes = scheduler.ChooseMultiLanes(time, Math.Min(chordSize, remaining), slot);
            if (lanes.Length == 0)
            {
                slot++;
                continue;
            }

            foreach (int lane in lanes)
                scheduler.Add(lane, time, time, OsuPlayObjectKind.Multi);

            remaining -= lanes.Length;
            slot++;
        }

        if (remaining > 0)
            MelonLogger.Warning($"[ManiaInMuse] Multi at {note.TimeSec:F3}s fallback could not place {remaining}/{hitCount} hits without lane overlap");
    }

    private static int ChooseMultiChordSize(int hitCount, float availableSec, int laneCount)
    {
        for (int chordSize = 1; chordSize <= laneCount; chordSize++)
        {
            int slots = (int)Math.Ceiling(hitCount / (double)chordSize);
            if (slots <= 1 || availableSec / (slots - 1) >= 0.1)
                return chordSize;
        }

        return laneCount;
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

    private static void EnsureMusicCollected(NoteInfo note, RuntimeLaneScheduler scheduler)
    {
        LanePosture target = note.IsAir ? LanePosture.Air : LanePosture.Ground;
        if (scheduler.IsPostureSatisfied(note.TimeSec, target, MusicWindowSec))
            return;

        AddTap(note.TimeSec, target, scheduler, boss: false, OsuPlayObjectKind.UtilityTap);
    }

    private static void EnsureBlockDodged(NoteInfo note, RuntimeLaneScheduler scheduler)
    {
        LanePosture unsafePosture = note.IsAir ? LanePosture.Air : LanePosture.Ground;
        LanePosture safePosture = unsafePosture == LanePosture.Air ? LanePosture.Ground : LanePosture.Air;

        if (scheduler.IsPostureSatisfied(note.TimeSec, safePosture, BlockWindowSec))
            return;
        if (!scheduler.IsPostureUnsafe(note.TimeSec, unsafePosture, BlockWindowSec))
            return;

        AddTap(note.TimeSec, safePosture, scheduler, boss: false, OsuPlayObjectKind.UtilityTap);
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

    private static class LocalSwapOptimizer
    {
        private const float SegmentGapSec = 0.75f;
        private const float ContextSec = 0.4f;
        private const float HardMinGapSec = 0.1f;
        private const float SoftMinGapSec = 0.2f;
        private const float SameTimeEpsilon = 0.0005f;
        private const double AcceptScoreDelta = 0.15;

        internal static void Optimize(List<OsuPlayObject> objects, PlayerConfig config)
        {
            if (objects.Count == 0 || config.KeyCount < 2)
                return;

            int optimizedRuns = 0;
            var chords = BuildChords(objects, config.OptimizerChordWindowSec);
            if (chords.Count >= config.OptimizerMinConsecutiveChords)
            {
                for (int i = 0; i < chords.Count;)
                {
                    if (!chords[i].IsTrigger(config))
                    {
                        i++;
                        continue;
                    }

                    int start = i;
                    int end = i;
                    while (end + 1 < chords.Count
                        && chords[end + 1].IsTrigger(config)
                        && chords[end + 1].TimeSec - chords[end].TimeSec <= SegmentGapSec)
                    {
                        end++;
                    }

                    int runLength = end - start + 1;
                    if (runLength >= config.OptimizerMinConsecutiveChords)
                    {
                        var run = chords.GetRange(start, runLength);
                        if (TryOptimizeRun(objects, run, config))
                            optimizedRuns++;
                    }

                    i = end + 1;
                }
            }

            int repairedSegments = config.EnableShortGapRepair ? RepairShortGaps(objects, config) : 0;
            if (optimizedRuns > 0)
                MelonLogger.Msg($"[ManiaInMuse] Local swap optimized {optimizedRuns} dense chord run(s)");
            if (repairedSegments > 0)
                MelonLogger.Msg($"[ManiaInMuse] Short gap repaired {repairedSegments} segment(s)");
        }

        private static List<Chord> BuildChords(List<OsuPlayObject> objects, float windowSec)
        {
            var candidates = objects
                .Select((Obj, Index) => new IndexedObject(Index, Obj))
                .Where(x => x.Obj.IsLocalSwapCandidate)
                .OrderBy(x => x.Obj.StartSec)
                .ThenBy(x => x.Index)
                .ToList();

            var chords = new List<Chord>();
            Chord current = null;
            foreach (var candidate in candidates)
            {
                if (current == null || candidate.Obj.StartSec - current.TimeSec > windowSec)
                {
                    current = new Chord(candidate.Obj.StartSec);
                    chords.Add(current);
                }

                current.Indices.Add(candidate.Index);
            }

            return chords;
        }

        private static bool TryOptimizeRun(List<OsuPlayObject> objects, List<Chord> run, PlayerConfig config)
        {
            double baselineScore = ScoreArrangement(objects, null, run, config);
            Dictionary<int, int> bestMap = null;
            double bestScore = baselineScore;

            foreach (bool startLeft in new[] { true, false })
            {
                if (!TryBuildAlternatingCandidate(objects, run, config, startLeft, out var candidateMap))
                    continue;

                double score = ScoreArrangement(objects, candidateMap, run, config);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestMap = candidateMap;
                }
            }

            if (bestMap == null || bestScore < baselineScore + AcceptScoreDelta)
                return false;

            foreach (var pair in bestMap)
            {
                if (objects[pair.Key].Lane != pair.Value)
                    objects[pair.Key] = objects[pair.Key].WithLane(pair.Value);
            }

            return true;
        }

        private static bool TryBuildAlternatingCandidate(
            List<OsuPlayObject> objects,
            List<Chord> run,
            PlayerConfig config,
            bool startLeft,
            out Dictionary<int, int> map)
        {
            map = new Dictionary<int, int>();
            var movable = run.SelectMany(c => c.Indices).ToHashSet();

            for (int slot = 0; slot < run.Count; slot++)
            {
                bool preferLeft = slot % 2 == 0 ? startLeft : !startLeft;
                var usedInChord = new HashSet<int>();
                var orderedIndices = run[slot].Indices
                    .OrderBy(index => objects[index].AllowsAnyPosture ? 1 : 0)
                    .ThenBy(index => objects[index].Lane)
                    .ToList();

                foreach (int index in orderedIndices)
                {
                    var obj = objects[index];
                    int lane = ChooseLaneForChordObject(objects, obj, index, preferLeft, usedInChord, movable, map, config);
                    if (lane <= 0)
                        return false;

                    map[index] = lane;
                    usedInChord.Add(lane);
                }
            }

            return map.Count > 0;
        }

        private static int ChooseLaneForChordObject(
            List<OsuPlayObject> objects,
            OsuPlayObject obj,
            int index,
            bool preferLeft,
            HashSet<int> usedInChord,
            HashSet<int> movable,
            Dictionary<int, int> map,
            PlayerConfig config)
        {
            int[] laneOrder = BuildLanePreference(obj, preferLeft, config);
            int bestLane = 0;
            double bestScore = double.NegativeInfinity;

            foreach (int lane in laneOrder)
            {
                if (!IsLaneAvailable(objects, obj, index, lane, usedInChord, movable, map, config))
                    continue;

                double score = CandidateLaneScore(objects, obj, index, lane, movable, map, config);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestLane = lane;
                }
            }

            return bestLane;
        }

        private static int[] BuildLanePreference(OsuPlayObject obj, bool preferLeft, PlayerConfig config)
        {
            bool? air = obj.AllowsAnyPosture ? null : config.IsAirLane(obj.Lane);
            var preferred = OrderedLanes(config, preferLeft, air);
            var secondary = OrderedLanes(config, !preferLeft, air);

            if (obj.AllowsAnyPosture)
                return preferred.Concat(secondary).Distinct().ToArray();

            return preferred
                .Concat(secondary)
                .Concat(OrderedLanes(config, preferLeft, null))
                .Concat(OrderedLanes(config, !preferLeft, null))
                .Distinct()
                .ToArray();
        }

        private static IEnumerable<int> OrderedLanes(PlayerConfig config, bool leftSide, bool? air)
        {
            return config.AllLaneIndexes
                .Where(lane => config.IsLeftSide(lane) == leftSide)
                .Where(lane => !air.HasValue || config.IsAirLane(lane) == air.Value)
                .OrderBy(lane => Math.Abs(config.LaneToX(lane) - 256))
                .ThenBy(lane => config.LaneToX(lane));
        }

        private static bool IsLaneAvailable(
            List<OsuPlayObject> objects,
            OsuPlayObject obj,
            int index,
            int lane,
            HashSet<int> usedInChord,
            HashSet<int> movable,
            Dictionary<int, int> map,
            PlayerConfig config)
        {
            if (usedInChord.Contains(lane))
                return false;

            if (!obj.AllowsAnyPosture && config.IsAirLane(lane) != config.IsAirLane(obj.Lane))
                return false;

            for (int i = 0; i < objects.Count; i++)
            {
                if (i == index)
                    continue;

                bool isUnmappedMovable = movable.Contains(i) && !map.ContainsKey(i);
                if (isUnmappedMovable)
                    continue;

                var other = objects[i];
                int otherLane = FinalLane(other, i, map);
                if (otherLane != lane)
                    continue;

                if (Math.Abs(other.StartSec - obj.StartSec) < SameTimeEpsilon)
                    return false;

                if (other.IsHold && RangesOverlap(other.StartSec, other.EndSec, obj.StartSec, obj.EndSec))
                    return false;
            }

            return true;
        }

        private static double CandidateLaneScore(
            List<OsuPlayObject> objects,
            OsuPlayObject obj,
            int index,
            int lane,
            HashSet<int> movable,
            Dictionary<int, int> map,
            PlayerConfig config)
        {
            float previousEnd = float.MinValue;
            float nextStart = float.MaxValue;
            for (int i = 0; i < objects.Count; i++)
            {
                if (i == index)
                    continue;
                if (movable.Contains(i) && !map.ContainsKey(i))
                    continue;

                var other = objects[i];
                if (FinalLane(other, i, map) != lane)
                    continue;

                if (other.EndSec <= obj.StartSec)
                    previousEnd = Math.Max(previousEnd, other.EndSec);
                if (other.StartSec >= obj.StartSec)
                    nextStart = Math.Min(nextStart, other.StartSec);
            }

            float previousGap = previousEnd == float.MinValue ? 1f : obj.StartSec - previousEnd;
            float nextGap = nextStart == float.MaxValue ? 1f : nextStart - obj.StartSec;
            double score = Math.Min(previousGap, nextGap);
            if (lane == obj.Lane)
                score += 0.02;
            if (config.IsLeftSide(lane) == config.IsLeftSide(obj.Lane))
                score += 0.01;
            return score;
        }

        private static double ScoreArrangement(List<OsuPlayObject> objects, Dictionary<int, int> map, List<Chord> run, PlayerConfig config)
        {
            map ??= new Dictionary<int, int>();
            float contextStart = run[0].TimeSec - ContextSec;
            float contextEnd = run[^1].TimeSec + ContextSec;

            var contextObjects = objects
                .Select((Obj, Index) => new ArrangedObject(Index, Obj, FinalLane(Obj, Index, map)))
                .Where(x => x.Obj.EndSec >= contextStart && x.Obj.StartSec <= contextEnd)
                .ToList();

            foreach (var arranged in contextObjects)
            {
                if (arranged.Obj.IsLocalSwapCandidate
                    && !arranged.Obj.AllowsAnyPosture
                    && config.IsAirLane(arranged.Lane) != config.IsAirLane(arranged.Obj.Lane))
                {
                    return double.NegativeInfinity;
                }
            }

            for (int i = 0; i < contextObjects.Count; i++)
            {
                var a = contextObjects[i];
                for (int j = i + 1; j < contextObjects.Count; j++)
                {
                    var b = contextObjects[j];
                    if (a.Lane != b.Lane)
                        continue;

                    if (Math.Abs(a.Obj.StartSec - b.Obj.StartSec) < SameTimeEpsilon)
                        return double.NegativeInfinity;
                    if ((a.Obj.IsHold || b.Obj.IsHold) && RangesOverlap(a.Obj.StartSec, a.Obj.EndSec, b.Obj.StartSec, b.Obj.EndSec))
                        return double.NegativeInfinity;
                }
            }

            double score = 0;
            foreach (var laneGroup in contextObjects.GroupBy(x => x.Lane))
            {
                var ordered = laneGroup.OrderBy(x => x.Obj.StartSec).ToList();
                for (int i = 1; i < ordered.Count; i++)
                {
                    float gap = ordered[i].Obj.StartSec - ordered[i - 1].Obj.EndSec;
                    if (gap < HardMinGapSec - SameTimeEpsilon)
                        return double.NegativeInfinity;
                    if (gap < SoftMinGapSec)
                        score -= (SoftMinGapSec - gap) * 30.0;
                }
            }

            for (int i = 0; i < run.Count; i++)
            {
                int left = 0;
                int right = 0;
                foreach (int index in run[i].Indices)
                {
                    if (config.IsLeftSide(FinalLane(objects[index], index, map)))
                        left++;
                    else
                        right++;
                }

                int sameSideCount = Math.Max(left, right);
                int splitSideCount = Math.Min(left, right);
                score += sameSideCount / (double)Math.Max(1, run[i].Indices.Count);
                score -= splitSideCount * 0.5;

                if (i == 0)
                    continue;

                bool previousLeft = MajoritySideIsLeft(objects, run[i - 1], map, config);
                bool currentLeft = MajoritySideIsLeft(objects, run[i], map, config);
                score += previousLeft != currentLeft ? 3.0 : -2.0;
            }

            foreach (var pair in map)
            {
                if (objects[pair.Key].Lane != pair.Value)
                    score -= 0.04;
            }

            return score;
        }

        private static bool MajoritySideIsLeft(List<OsuPlayObject> objects, Chord chord, Dictionary<int, int> map, PlayerConfig config)
        {
            int left = 0;
            int right = 0;
            foreach (int index in chord.Indices)
            {
                if (config.IsLeftSide(FinalLane(objects[index], index, map)))
                    left++;
                else
                    right++;
            }

            return left > right;
        }

        private static int FinalLane(OsuPlayObject obj, int index, Dictionary<int, int> map)
        {
            return map != null && map.TryGetValue(index, out int lane) ? lane : obj.Lane;
        }

        private static bool RangesOverlap(float aStart, float aEnd, float bStart, float bEnd)
        {
            return aStart < bEnd + SameTimeEpsilon && aEnd > bStart - SameTimeEpsilon;
        }

        private static int RepairShortGaps(List<OsuPlayObject> objects, PlayerConfig config)
        {
            var windows = FindShortGapWindows(objects, config);
            if (windows.Count == 0)
                return 0;

            var segments = BuildRepairSegments(objects, windows, config);
            int repaired = 0;
            foreach (var segment in segments)
            {
                if (TryRepairShortGapSegment(objects, segment, config))
                    repaired++;
            }

            return repaired;
        }

        private static List<RepairWindow> FindShortGapWindows(List<OsuPlayObject> objects, PlayerConfig config)
        {
            var windows = new List<RepairWindow>();
            var indexed = objects
                .Select((Obj, Index) => new IndexedObject(Index, Obj))
                .OrderBy(x => x.Obj.Lane)
                .ThenBy(x => x.Obj.StartSec)
                .ToList();

            foreach (var laneGroup in indexed.GroupBy(x => x.Obj.Lane))
            {
                var ordered = laneGroup.ToList();
                for (int i = 1; i < ordered.Count; i++)
                {
                    var previous = ordered[i - 1];
                    var current = ordered[i];
                    float gap = current.Obj.StartSec - previous.Obj.EndSec;
                    if (gap >= config.ShortGapTargetSec)
                        continue;
                    if (!previous.Obj.IsLocalSwapCandidate && !current.Obj.IsLocalSwapCandidate)
                        continue;

                    windows.Add(new RepairWindow(
                        Math.Min(previous.Obj.StartSec, current.Obj.StartSec),
                        Math.Max(previous.Obj.StartSec, current.Obj.StartSec)));
                }
            }

            return windows;
        }

        private static List<RepairSegment> BuildRepairSegments(List<OsuPlayObject> objects, List<RepairWindow> windows, PlayerConfig config)
        {
            var movable = objects
                .Select((Obj, Index) => new IndexedObject(Index, Obj))
                .Where(x => x.Obj.IsLocalSwapCandidate)
                .OrderBy(x => x.Obj.StartSec)
                .ThenBy(x => x.Index)
                .ToList();

            if (movable.Count < 2)
                return new List<RepairSegment>();

            const float maxSegmentSec = 3.0f;
            var raw = new List<RepairWindow>();
            foreach (var window in windows)
            {
                float start = window.StartSec - config.ShortGapSegmentPaddingSec;
                float end = window.EndSec + config.ShortGapSegmentPaddingSec;
                int first = movable.FindIndex(x => x.Obj.StartSec >= start);
                if (first < 0)
                    continue;

                int last = first;
                while (last + 1 < movable.Count && movable[last + 1].Obj.StartSec <= end)
                    last++;

                while (first > 0
                    && movable[first].Obj.StartSec - movable[first - 1].Obj.StartSec <= config.ShortGapSegmentBreakSec
                    && movable[last].Obj.StartSec - movable[first - 1].Obj.StartSec <= maxSegmentSec)
                {
                    first--;
                }

                while (last + 1 < movable.Count
                    && movable[last + 1].Obj.StartSec - movable[last].Obj.StartSec <= config.ShortGapSegmentBreakSec
                    && movable[last + 1].Obj.StartSec - movable[first].Obj.StartSec <= maxSegmentSec)
                {
                    last++;
                }

                raw.Add(new RepairWindow(movable[first].Obj.StartSec, movable[last].Obj.StartSec));
            }

            if (raw.Count == 0)
                return new List<RepairSegment>();

            raw.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
            var merged = new List<RepairWindow>();
            RepairWindow current = raw[0];
            for (int i = 1; i < raw.Count; i++)
            {
                var next = raw[i];
                if (next.StartSec <= current.EndSec + 0.05f)
                    current = new RepairWindow(current.StartSec, Math.Max(current.EndSec, next.EndSec));
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }

            merged.Add(current);

            var segments = new List<RepairSegment>();
            foreach (var range in merged)
            {
                var indices = movable
                    .Where(x => x.Obj.StartSec >= range.StartSec - SameTimeEpsilon && x.Obj.StartSec <= range.EndSec + SameTimeEpsilon)
                    .Select(x => x.Index)
                    .Distinct()
                    .ToList();

                if (indices.Count >= 2)
                    segments.Add(new RepairSegment(range.StartSec, range.EndSec, indices));
            }

            return segments;
        }

        private static bool TryRepairShortGapSegment(List<OsuPlayObject> objects, RepairSegment segment, PlayerConfig config)
        {
            var baseline = EvaluateShortGapSegment(objects, null, segment, config);
            if (baseline.Valid && baseline.ShortGapCount == 0)
                return false;

            Dictionary<int, int> bestMap = null;
            ShortGapScore bestScore = default;
            bool hasBest = false;

            foreach (bool preferLeftTie in new[] { true, false })
            {
                if (!TryBuildShortGapCandidate(objects, segment, config, preferLeftTie, out var candidateMap))
                    continue;

                var candidateScore = EvaluateShortGapSegment(objects, candidateMap, segment, config);
                if (!IsBetterRepair(candidateScore, hasBest ? bestScore : baseline))
                    continue;

                bestMap = candidateMap;
                bestScore = candidateScore;
                hasBest = true;
            }

            if (!hasBest || !IsBetterRepair(bestScore, baseline))
                return false;

            foreach (var pair in bestMap)
            {
                if (objects[pair.Key].Lane != pair.Value)
                    objects[pair.Key] = objects[pair.Key].WithLane(pair.Value);
            }

            return true;
        }

        private static bool TryBuildShortGapCandidate(
            List<OsuPlayObject> objects,
            RepairSegment segment,
            PlayerConfig config,
            bool preferLeftTie,
            out Dictionary<int, int> map)
        {
            map = new Dictionary<int, int>();
            var movable = segment.Indices.ToHashSet();
            var activeMap = config.AllLaneIndexes.ToDictionary(lane => lane, lane => lane);
            var chords = BuildChordsForIndices(objects, segment.Indices, config.OptimizerChordWindowSec);

            foreach (var chord in chords)
            {
                var usedInChord = new HashSet<int>();
                var ordered = chord.Indices
                    .OrderByDescending(index => CurrentLanePressure(objects, index, config))
                    .ThenBy(index => objects[index].AllowsAnyPosture ? 1 : 0)
                    .ThenBy(index => objects[index].Lane)
                    .ToList();

                foreach (int index in ordered)
                {
                    var obj = objects[index];
                    int lane = ChooseShortGapLane(objects, obj, index, usedInChord, movable, map, activeMap, config, preferLeftTie);
                    if (lane <= 0)
                        return false;

                    map[index] = lane;
                    usedInChord.Add(lane);

                    if (!obj.AllowsAnyPosture)
                        ContinueSwap(activeMap, obj.Lane, lane);
                }
            }

            return map.Count > 0;
        }

        private static List<Chord> BuildChordsForIndices(List<OsuPlayObject> objects, List<int> indices, float windowSec)
        {
            var ordered = indices
                .OrderBy(index => objects[index].StartSec)
                .ThenBy(index => index)
                .ToList();

            var chords = new List<Chord>();
            Chord current = null;
            foreach (int index in ordered)
            {
                var obj = objects[index];
                if (current == null || obj.StartSec - current.TimeSec > windowSec)
                {
                    current = new Chord(obj.StartSec);
                    chords.Add(current);
                }

                current.Indices.Add(index);
            }

            return chords;
        }

        private static int ChooseShortGapLane(
            List<OsuPlayObject> objects,
            OsuPlayObject obj,
            int index,
            HashSet<int> usedInChord,
            HashSet<int> movable,
            Dictionary<int, int> map,
            Dictionary<int, int> activeMap,
            PlayerConfig config,
            bool preferLeftTie)
        {
            int activeLane = activeMap.TryGetValue(obj.Lane, out int mappedLane) ? mappedLane : obj.Lane;
            int[] laneOrder = ShortGapLaneCandidates(obj, activeLane, config);
            int bestLane = 0;
            double bestScore = double.NegativeInfinity;

            foreach (int lane in laneOrder)
            {
                if (!IsLaneAvailable(objects, obj, index, lane, usedInChord, movable, map, config))
                    continue;

                double score = ShortGapLaneScore(objects, obj, index, lane, movable, map, activeLane, config, preferLeftTie);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestLane = lane;
                }
            }

            return bestLane;
        }

        private static int[] ShortGapLaneCandidates(OsuPlayObject obj, int activeLane, PlayerConfig config)
        {
            IEnumerable<int> lanes = obj.AllowsAnyPosture
                ? config.AllLaneIndexes
                : config.AllLaneIndexes.Where(lane => config.IsAirLane(lane) == config.IsAirLane(obj.Lane));

            return lanes
                .OrderBy(lane => lane == activeLane ? 0 : 1)
                .ThenBy(lane => Math.Abs(config.LaneToX(lane) - config.LaneToX(activeLane)))
                .ThenBy(lane => config.LaneToX(lane))
                .ToArray();
        }

        private static double ShortGapLaneScore(
            List<OsuPlayObject> objects,
            OsuPlayObject obj,
            int index,
            int lane,
            HashSet<int> movable,
            Dictionary<int, int> map,
            int activeLane,
            PlayerConfig config,
            bool preferLeftTie)
        {
            float previousEnd = float.MinValue;
            float nextStart = float.MaxValue;
            for (int i = 0; i < objects.Count; i++)
            {
                if (i == index)
                    continue;
                if (movable.Contains(i) && !map.ContainsKey(i))
                    continue;

                var other = objects[i];
                if (FinalLane(other, i, map) != lane)
                    continue;

                if (other.EndSec <= obj.StartSec)
                    previousEnd = Math.Max(previousEnd, other.EndSec);
                if (other.StartSec >= obj.StartSec)
                    nextStart = Math.Min(nextStart, other.StartSec);
            }

            float previousGap = previousEnd == float.MinValue ? config.ShortGapTargetSec : obj.StartSec - previousEnd;
            float nextGap = nextStart == float.MaxValue ? config.ShortGapTargetSec : nextStart - obj.StartSec;
            float minGap = Math.Min(previousGap, nextGap);
            double score = Math.Min(minGap, config.ShortGapTargetSec) * 10.0;
            if (minGap < config.ShortGapTargetSec)
                score -= (config.ShortGapTargetSec - minGap) * 60.0;
            if (minGap < config.ShortGapHardMinSec)
                score -= 1000.0;
            if (lane == activeLane)
                score += 0.08;
            if (lane == obj.Lane)
                score += 0.02;
            if (config.IsLeftSide(lane) == preferLeftTie)
                score += 0.005;

            return score;
        }

        private static float CurrentLanePressure(List<OsuPlayObject> objects, int index, PlayerConfig config)
        {
            var obj = objects[index];
            float previousEnd = float.MinValue;
            float nextStart = float.MaxValue;
            for (int i = 0; i < objects.Count; i++)
            {
                if (i == index || objects[i].Lane != obj.Lane)
                    continue;

                if (objects[i].EndSec <= obj.StartSec)
                    previousEnd = Math.Max(previousEnd, objects[i].EndSec);
                if (objects[i].StartSec >= obj.StartSec)
                    nextStart = Math.Min(nextStart, objects[i].StartSec);
            }

            float previousGap = previousEnd == float.MinValue ? config.ShortGapTargetSec : obj.StartSec - previousEnd;
            float nextGap = nextStart == float.MaxValue ? config.ShortGapTargetSec : nextStart - obj.StartSec;
            return Math.Max(0, config.ShortGapTargetSec - Math.Min(previousGap, nextGap));
        }

        private static void ContinueSwap(Dictionary<int, int> activeMap, int originalLane, int chosenLane)
        {
            int currentLane = activeMap.TryGetValue(originalLane, out int current) ? current : originalLane;
            if (currentLane == chosenLane)
                return;

            int otherOriginal = 0;
            foreach (var pair in activeMap)
            {
                if (pair.Value == chosenLane)
                {
                    otherOriginal = pair.Key;
                    break;
                }
            }

            activeMap[originalLane] = chosenLane;
            if (otherOriginal > 0 && otherOriginal != originalLane)
                activeMap[otherOriginal] = currentLane;
        }

        private static ShortGapScore EvaluateShortGapSegment(List<OsuPlayObject> objects, Dictionary<int, int> map, RepairSegment segment, PlayerConfig config)
        {
            map ??= new Dictionary<int, int>();
            var segmentSet = segment.Indices.ToHashSet();
            float contextStart = segment.StartSec - config.ShortGapSegmentPaddingSec;
            float contextEnd = segment.EndSec + config.ShortGapSegmentPaddingSec;
            var contextObjects = objects
                .Select((Obj, Index) => new ArrangedObject(Index, Obj, FinalLane(Obj, Index, map)))
                .Where(x => x.Obj.EndSec >= contextStart && x.Obj.StartSec <= contextEnd)
                .ToList();

            foreach (var arranged in contextObjects)
            {
                if (arranged.Obj.IsLocalSwapCandidate
                    && !arranged.Obj.AllowsAnyPosture
                    && config.IsAirLane(arranged.Lane) != config.IsAirLane(arranged.Obj.Lane))
                {
                    return ShortGapScore.Invalid;
                }
            }

            for (int i = 0; i < contextObjects.Count; i++)
            {
                var a = contextObjects[i];
                for (int j = i + 1; j < contextObjects.Count; j++)
                {
                    var b = contextObjects[j];
                    if (a.Lane != b.Lane)
                        continue;

                    if (Math.Abs(a.Obj.StartSec - b.Obj.StartSec) < SameTimeEpsilon)
                        return ShortGapScore.Invalid;
                    if ((a.Obj.IsHold || b.Obj.IsHold) && RangesOverlap(a.Obj.StartSec, a.Obj.EndSec, b.Obj.StartSec, b.Obj.EndSec))
                        return ShortGapScore.Invalid;
                }
            }

            double score = 0;
            int shortGapCount = 0;
            float minGap = float.MaxValue;

            foreach (var laneGroup in contextObjects.GroupBy(x => x.Lane))
            {
                var ordered = laneGroup.OrderBy(x => x.Obj.StartSec).ToList();
                for (int i = 1; i < ordered.Count; i++)
                {
                    float gap = ordered[i].Obj.StartSec - ordered[i - 1].Obj.EndSec;
                    bool relevant = segmentSet.Contains(ordered[i].Index) || segmentSet.Contains(ordered[i - 1].Index);
                    if (!relevant)
                        continue;
                    if (gap < config.ShortGapHardMinSec - SameTimeEpsilon)
                        return ShortGapScore.Invalid;

                    minGap = Math.Min(minGap, gap);
                    score += Math.Min(gap, config.ShortGapTargetSec) * 12.0;
                    if (gap < config.ShortGapTargetSec)
                    {
                        shortGapCount++;
                        score -= (config.ShortGapTargetSec - gap) * 80.0;
                    }
                }
            }

            int left = 0;
            int right = 0;
            foreach (int index in segment.Indices)
            {
                if (config.IsLeftSide(FinalLane(objects[index], index, map)))
                    left++;
                else
                    right++;
            }

            score -= Math.Abs(left - right) * 0.08;
            foreach (var pair in map)
            {
                if (objects[pair.Key].Lane != pair.Value)
                    score -= 0.03;
            }

            if (minGap == float.MaxValue)
                minGap = config.ShortGapTargetSec;

            return new ShortGapScore(true, score, shortGapCount, minGap);
        }

        private static bool IsBetterRepair(ShortGapScore candidate, ShortGapScore baseline)
        {
            if (!candidate.Valid)
                return false;
            if (!baseline.Valid)
                return true;
            if (candidate.ShortGapCount < baseline.ShortGapCount)
                return candidate.Score >= baseline.Score - 1.0;
            if (candidate.ShortGapCount == baseline.ShortGapCount
                && candidate.MinGap > baseline.MinGap + 0.015f)
            {
                return candidate.Score > baseline.Score + 0.05;
            }

            return candidate.Score > baseline.Score + 0.35;
        }

        private readonly struct IndexedObject
        {
            internal readonly int Index;
            internal readonly OsuPlayObject Obj;

            internal IndexedObject(int index, OsuPlayObject obj)
            {
                Index = index;
                Obj = obj;
            }
        }

        private readonly struct ArrangedObject
        {
            internal readonly int Index;
            internal readonly OsuPlayObject Obj;
            internal readonly int Lane;

            internal ArrangedObject(int index, OsuPlayObject obj, int lane)
            {
                Index = index;
                Obj = obj;
                Lane = lane;
            }
        }

        private sealed class Chord
        {
            internal readonly float TimeSec;
            internal readonly List<int> Indices = new();

            internal Chord(float timeSec)
            {
                TimeSec = timeSec;
            }

            internal bool IsTrigger(PlayerConfig config)
            {
                return Indices.Count >= config.OptimizerMinTriggerChordCount;
            }
        }

        private readonly struct RepairWindow
        {
            internal readonly float StartSec;
            internal readonly float EndSec;

            internal RepairWindow(float startSec, float endSec)
            {
                StartSec = startSec;
                EndSec = endSec;
            }
        }

        private sealed class RepairSegment
        {
            internal readonly float StartSec;
            internal readonly float EndSec;
            internal readonly List<int> Indices;

            internal RepairSegment(float startSec, float endSec, List<int> indices)
            {
                StartSec = startSec;
                EndSec = endSec;
                Indices = indices;
            }
        }

        private readonly struct ShortGapScore
        {
            internal static readonly ShortGapScore Invalid = new(false, double.NegativeInfinity, int.MaxValue, 0);

            internal readonly bool Valid;
            internal readonly double Score;
            internal readonly int ShortGapCount;
            internal readonly float MinGap;

            internal ShortGapScore(bool valid, double score, int shortGapCount, float minGap)
            {
                Valid = valid;
                Score = score;
                ShortGapCount = shortGapCount;
                MinGap = minGap;
            }
        }
    }

    private readonly struct MultiPattern
    {
        private readonly int[] _leftLanes;
        private readonly int[] _rightLanes;

        internal MultiPattern(int[] leftLanes, int[] rightLanes)
        {
            _leftLanes = leftLanes ?? Array.Empty<int>();
            _rightLanes = rightLanes ?? Array.Empty<int>();
        }

        private int[] LeftLanes => _leftLanes ?? Array.Empty<int>();
        private int[] RightLanes => _rightLanes ?? Array.Empty<int>();

        internal bool IsUsable => LeftLanes.Length > 0 && RightLanes.Length > 0;
        internal int LaneCount => LeftLanes.Length + RightLanes.Length;

        internal int SlotsNeeded(int hitCount)
        {
            if (!IsUsable)
                return 0;

            int[] leftLanes = LeftLanes;
            int[] rightLanes = RightLanes;
            int remaining = hitCount;
            int slots = 0;
            while (remaining > 0)
            {
                int capacity = slots % 2 == 0 ? leftLanes.Length : rightLanes.Length;
                remaining -= Math.Min(remaining, capacity);
                slots++;
            }

            return slots;
        }

        internal double LaneLoadSpread(int hitCount)
        {
            if (!IsUsable)
                return double.PositiveInfinity;

            var laneHits = new Dictionary<int, int>();
            foreach (int lane in LeftLanes.Concat(RightLanes))
                laneHits[lane] = 0;

            int remaining = hitCount;
            int slot = 0;
            while (remaining > 0)
            {
                int[] lanes = LanesForSlot(slot, remaining);
                foreach (int lane in lanes)
                    laneHits[lane]++;

                remaining -= lanes.Length;
                slot++;
            }

            double average = laneHits.Values.Average();
            return laneHits.Values.Sum(v => Math.Abs(v - average));
        }

        internal int[] LanesForSlot(int slot, int count)
        {
            int[] source = slot % 2 == 0 ? LeftLanes : RightLanes;
            return source.Take(Math.Min(count, source.Length)).ToArray();
        }
    }

    private sealed class RuntimeLaneScheduler
    {
        private readonly List<OsuPlayObject> _objects;
        private readonly PlayerConfig _config;

        internal RuntimeLaneScheduler(List<OsuPlayObject> objects, PlayerConfig config)
        {
            _objects = objects;
            _config = config;
        }

        internal int LaneCount => _config.KeyCount;

        internal void Add(int lane, float startSec, float endSec, OsuPlayObjectKind kind = OsuPlayObjectKind.RegularTap)
        {
            _objects.Add(new OsuPlayObject(lane, startSec, Math.Max(startSec, endSec), endSec > startSec, kind));
        }

        internal int ChooseLane(float startSec, float endSec, LanePosture posture, bool boss)
        {
            int[] lanes = boss ? _config.AllLaneIndexes : _config.LaneIndexesFor(posture);
            if (lanes.Length == 0)
                lanes = _config.AllLaneIndexes;

            int bestLane = lanes[0];
            double bestScore = double.NegativeInfinity;
            bool foundFreeLane = false;

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
                    foundFreeLane = true;
                    bestScore = score;
                    bestLane = lane;
                }
            }

            if (!foundFreeLane)
                MelonLogger.Warning($"[ManiaInMuse] No free {posture} lane at {startSec:F3}s; using lane {bestLane}");

            return bestLane;
        }

        internal MultiPattern ChooseMultiPattern(float startSec, float endSec, int hitCount, float availableSec)
        {
            var leftAvailable = SideAvailableLanes(startSec, endSec, leftSide: true);
            var rightAvailable = SideAvailableLanes(startSec, endSec, leftSide: false);
            if (leftAvailable.Count == 0 || rightAvailable.Count == 0)
                return default;

            MultiPattern best = default;
            double bestScore = double.NegativeInfinity;
            for (int leftCount = 1; leftCount <= leftAvailable.Count; leftCount++)
            {
                int[] leftGroup = FindBestAdjacentGroup(startSec, endSec, leftSide: true, leftCount);
                if (leftGroup.Length != leftCount)
                    continue;

                for (int rightCount = 1; rightCount <= rightAvailable.Count; rightCount++)
                {
                    int[] rightGroup = FindBestAdjacentGroup(startSec, endSec, leftSide: false, rightCount);
                    if (rightGroup.Length != rightCount)
                        continue;

                    var pattern = new MultiPattern(leftGroup, rightGroup);
                    int slots = pattern.SlotsNeeded(hitCount);
                    if (slots <= 0)
                        continue;

                    double naturalGap = slots <= 1 ? availableSec : availableSec / (slots - 1);
                    if (slots > 1 && naturalGap < 0.1)
                        continue;

                    double score = ScoreMultiPattern(pattern, hitCount, slots, naturalGap, leftAvailable.Count + rightAvailable.Count);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = pattern;
                    }
                }
            }

            return best;
        }

        internal bool AreLanesFreeAt(int[] lanes, float timeSec)
        {
            return lanes.Length > 0
                && lanes.All(lane => !HasObjectAt(lane, timeSec) && !HasHoldOverlap(lane, timeSec, timeSec));
        }

        internal int[] ChooseMultiLanes(float timeSec, int count, int slot)
        {
            var available = _config.AllLaneIndexes
                .Where(lane => !HasObjectAt(lane, timeSec) && !HasHoldOverlap(lane, timeSec, timeSec))
                .OrderBy(lane => _config.LaneToX(lane))
                .ToList();

            if (available.Count == 0)
                return Array.Empty<int>();

            bool preferLeft = slot % 2 == 0;
            var primary = available
                .Where(lane => _config.IsLeftSide(lane) == preferLeft)
                .OrderBy(lane => Math.Abs(_config.LaneToX(lane) - 256))
                .ToList();
            var secondary = available
                .Where(lane => _config.IsLeftSide(lane) != preferLeft)
                .OrderBy(lane => Math.Abs(_config.LaneToX(lane) - 256))
                .ToList();

            var source = primary.Count > 0 ? primary : secondary;
            return source.Take(Math.Min(count, source.Count)).ToArray();
        }

        private List<int> SideAvailableLanes(float startSec, float endSec, bool leftSide)
        {
            return _config.AllLaneIndexes
                .Where(lane => _config.IsLeftSide(lane) == leftSide)
                .Where(lane => !HasHoldOverlap(lane, startSec, endSec))
                .OrderBy(lane => _config.LaneToX(lane))
                .ToList();
        }

        private int[] FindBestAdjacentGroup(float startSec, float endSec, bool leftSide, int count)
        {
            var candidates = SideAvailableLanes(startSec, endSec, leftSide);
            if (candidates.Count < count)
                return Array.Empty<int>();

            var candidateSet = candidates.ToHashSet();
            int[] orderedAll = _config.AllLaneIndexes.OrderBy(lane => _config.LaneToX(lane)).ToArray();
            int[] best = Array.Empty<int>();
            double bestScore = double.PositiveInfinity;

            for (int i = 0; i <= orderedAll.Length - count; i++)
            {
                int[] group = orderedAll.Skip(i).Take(count).ToArray();
                if (group.Any(lane => !candidateSet.Contains(lane)))
                    continue;

                double center = group.Average(lane => _config.LaneToX(lane));
                double score = Math.Abs(center - 256);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = group;
                }
            }

            return best.Length == count
                ? best
                : candidates.OrderBy(lane => Math.Abs(_config.LaneToX(lane) - 256)).Take(count).ToArray();
        }

        private static double ScoreMultiPattern(MultiPattern pattern, int hitCount, int slots, double naturalGap, int availableLaneCount)
        {
            double gapScore;
            if (slots <= 1)
                gapScore = 0;
            else if (naturalGap <= 0.125)
                gapScore = 2.0 - Math.Abs(naturalGap - 0.1125) * 20.0;
            else
                gapScore = 1.0 - Math.Min(1.0, (naturalGap - 0.125) * 8.0);

            double usedLaneScore = pattern.LaneCount / (double)Math.Max(1, availableLaneCount);
            double spreadPenalty = pattern.LaneLoadSpread(hitCount) * 0.04;
            return gapScore + usedLaneScore - spreadPenalty;
        }

        internal bool IsPostureSatisfied(float timeSec, LanePosture target, float windowSec)
        {
            return PostureAt(timeSec - windowSec) == target
                || PostureAt(timeSec) == target
                || PostureAt(timeSec + windowSec) == target;
        }

        internal bool IsPostureUnsafe(float timeSec, LanePosture unsafePosture, float windowSec)
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
                && o.StartSec < Math.Max(startSec, endSec) + 0.0005f
                && o.EndSec > Math.Min(startSec, endSec) - 0.0005f);
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

                if (_config.IsLeftSide(obj.Lane))
                    left++;
                else
                    right++;
            }

            if (left == right)
                return 0;

            bool laneIsLeft = _config.IsLeftSide(lane);
            int diff = Math.Abs(left - right);
            return (left > right && !laneIsLeft) || (right > left && laneIsLeft)
                ? diff * 0.08
                : -diff * 0.08;
        }

        private LanePosture PostureAt(float timeSec)
        {
            int lastTimeCompare = int.MinValue;
            float lastTime = float.MinValue;
            bool airAtLastTime = false;
            bool groundAtLastTime = false;

            foreach (var obj in _objects)
            {
                if (obj.IsHold && obj.StartSec <= timeSec && obj.EndSec >= timeSec)
                    return _config.IsAirLane(obj.Lane) ? LanePosture.Air : LanePosture.Ground;

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
                    if (_config.IsAirLane(obj.Lane))
                        airAtLastTime = true;
                    else
                        groundAtLastTime = true;
                }
            }

            if (lastTimeCompare == int.MinValue)
                return LanePosture.Ground;
            if (groundAtLastTime)
                return LanePosture.Ground;
            if (airAtLastTime && timeSec <= lastTime + AirHoldSec)
                return LanePosture.Air;

            return LanePosture.Ground;
        }
    }
}

internal static class RuntimeOsuWriter
{
    private const string ExportDirectory = "UserData\\ManiaInMuse\\maps";

    internal static void SaveLatest(IReadOnlyList<OsuPlayObject> objects, float bpm, PlayerConfig config)
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
        sb.AppendLine($"CircleSize:{config.KeyCount}");
        sb.AppendLine("OverallDifficulty:8");
        sb.AppendLine();
        sb.AppendLine("[TimingPoints]");
        float beatLength = 60000f / Math.Max(1, bpm);
        sb.AppendLine($"0,{beatLength.ToString("0.############", CultureInfo.InvariantCulture)},4,2,1,60,1,0");
        sb.AppendLine();
        sb.AppendLine("[HitObjects]");

        foreach (var obj in objects.OrderBy(o => o.StartSec).ThenBy(o => o.Lane))
        {
            int x = config.LaneToX(obj.Lane);
            int startMs = ToMs(obj.StartSec);
            if (obj.IsHold)
                sb.AppendLine($"{x},192,{startMs},128,0,{ToMs(obj.EndSec)}:0:0:0:0:");
            else
                sb.AppendLine($"{x},192,{startMs},1,0,0:0:0:0:");
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        MelonLogger.Msg($"[ManiaInMuse] Runtime osu exported: {path} ({objects.Count} objects)");
    }

    private static int ToMs(float seconds)
    {
        return (int)Math.Round(seconds * 1000f, MidpointRounding.AwayFromZero);
    }
}
