using System.Globalization;
using System.Text;

namespace AccuracyIndicator;

internal static class RuntimeOsuMapBuilder
{
    private const float PreferredMinGapSec = 0.2f;
    private const float BalanceWindowSec = 4f;
    // 有效滞空时长：起跳后这段时间内可以获取空中音符、规避地面齿轮。
    // 滞空动画总长约 500ms，最后约 100ms 视为在地面，因此有效滞空约为 400ms。
    private const float AirHoldSec = 0.400f;
    // 滞空动画总时长：期间不能二段跳，必须落地后才能重新起跳。
    private const float AirborneAnimSec = 0.500f;
    // 齿轮落地边界：危险窗口 [0.400, 0.600]（有效滞空结束到落地 + 余量）。
    private const float GearRiskLowSec = 0.400f;
    private const float GearRiskHighSec = 0.600f;
    private const float MusicWindowSec = 0.05f;
    private const float BlockWindowSec = 0.12f;
    private const float MultiEndPaddingSec = 0.08f;

    // 阶段1的空地键描述：只含姿态（空/地）与时间，不含具体轨道。轨道在阶段2分配。
    private readonly struct KeyDesc
    {
        internal readonly float StartSec;
        internal readonly float EndSec;
        internal readonly LanePosture Posture;
        internal readonly OsuPlayObjectKind Kind;
        internal readonly int MultiSlot;

        internal KeyDesc(float startSec, float endSec, LanePosture posture, OsuPlayObjectKind kind, int multiSlot = -1)
        {
            StartSec = startSec;
            EndSec = Math.Max(startSec, endSec);
            Posture = posture;
            Kind = kind;
            MultiSlot = multiSlot;
        }

        internal bool IsHold => EndSec > StartSec;
    }

    internal static IReadOnlyList<OsuPlayObject> Build(IReadOnlyList<NoteInfo> notes, float bpm, PlayerConfig config)
    {
        // 阶段1：构建空地键集合（只定姿态与时间，不分配轨道）
        var keys = BuildKeyCollection(notes, bpm, config);

        // 阶段2：根据集合分配轨道
        return AssignLanes(keys, config);
    }

    // ===== 阶段1：构建空地键集合（只定姿态，不分轨道） =====

    private static List<KeyDesc> BuildKeyCollection(IReadOnlyList<NoteInfo> notes, float bpm, PlayerConfig config)
    {
        var keys = new List<KeyDesc>();
        var multiNotes = notes.Where(n => n.Type == 8).ToList();

        // 怪物/幽灵/长按/boss/multi：姿态来自谱面数据（boss 用平衡启发式，multi 用交替启发式）
        foreach (var note in notes.OrderBy(n => n.TimeSec))
        {
            if (note.Type != 8 && IsInsideMulti(note, multiNotes))
                continue;

            switch (note.Type)
            {
                case 1:
                case 4:
                    keys.Add(new KeyDesc(note.TimeSec, note.TimeSec, note.IsAir ? LanePosture.Air : LanePosture.Ground, OsuPlayObjectKind.RegularTap));
                    break;
                case 3:
                    if (note.EndTimeSec > note.TimeSec)
                        keys.Add(new KeyDesc(note.TimeSec, note.EndTimeSec, note.IsAir ? LanePosture.Air : LanePosture.Ground, OsuPlayObjectKind.Hold));
                    break;
                case 5:
                    keys.Add(new KeyDesc(note.TimeSec, note.TimeSec, DecideBossPosture(keys, note.TimeSec), OsuPlayObjectKind.BossTap));
                    break;
                case 8:
                    AddMultiKeys(keys, note, bpm, config);
                    break;
            }
        }

        // 齿轮落地修复：提前落地 + 重新起跳
        PlanGearLandings(keys, notes, multiNotes);

        // 障碍/音符：按姿态模拟补键
        foreach (var note in notes.Where(n => n.Type is 2 or 7).OrderBy(n => n.TimeSec))
        {
            if (IsInsideMulti(note, multiNotes))
                continue;

            if (note.Type == 7)
                EnsureMusicCollected(keys, note);
            else
                EnsureBlockDodged(keys, note);
        }

        keys.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        return keys;
    }

    // 姿态模拟：只追踪姿态序列（空/地），不看具体轨道。
    private static LanePosture PostureAt(List<KeyDesc> keys, float timeSec)
    {
        int lastTimeCompare = int.MinValue;
        float lastTime = float.MinValue;
        bool airAtLastTime = false;
        bool groundAtLastTime = false;

        foreach (var key in keys)
        {
            if (key.IsHold && key.StartSec <= timeSec && key.EndSec >= timeSec)
                return key.Posture;

            if (key.StartSec > timeSec)
                continue;

            int timeCompare = (int)Math.Round(key.StartSec * 1000f);
            if (timeCompare > lastTimeCompare)
            {
                lastTimeCompare = timeCompare;
                lastTime = key.StartSec;
                airAtLastTime = false;
                groundAtLastTime = false;
            }

            if (timeCompare == lastTimeCompare)
            {
                if (key.Posture == LanePosture.Air)
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

    private static bool IsPostureSatisfied(List<KeyDesc> keys, float timeSec, LanePosture target, float windowSec)
    {
        return PostureAt(keys, timeSec - windowSec) == target
            || PostureAt(keys, timeSec) == target
            || PostureAt(keys, timeSec + windowSec) == target;
    }

    private static bool IsPostureUnsafe(List<KeyDesc> keys, float timeSec, LanePosture unsafePosture, float windowSec)
    {
        return PostureAt(keys, timeSec - windowSec) == unsafePosture
            || PostureAt(keys, timeSec) == unsafePosture
            || PostureAt(keys, timeSec + windowSec) == unsafePosture;
    }

    // boss 可以空/地任选，阶段1用平衡启发式决定其姿态，阶段2再落到具体轨道。
    private static LanePosture DecideBossPosture(List<KeyDesc> keys, float timeSec)
    {
        int air = 0;
        int ground = 0;
        float begin = timeSec - BalanceWindowSec;
        foreach (var key in keys)
        {
            if (key.StartSec < begin || key.StartSec > timeSec)
                continue;

            if (key.Posture == LanePosture.Air)
                air++;
            else
                ground++;
        }

        return ground > air ? LanePosture.Air : LanePosture.Ground;
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

    private static void AddMultiKeys(List<KeyDesc> keys, NoteInfo note, float bpm, PlayerConfig config)
    {
        int hitCount = Math.Max(1, note.MultiMaxHitCount);
        float endSec = note.EndTimeSec > note.TimeSec ? note.EndTimeSec : note.TimeSec + Math.Max(note.MultiDurationSec, 0);
        float available = Math.Max(0, endSec - note.TimeSec - MultiEndPaddingSec);
        if (available <= 0 || hitCount == 1)
        {
            keys.Add(new KeyDesc(note.TimeSec, note.TimeSec, LanePosture.Air, OsuPlayObjectKind.Multi, 0));
            return;
        }

        int chordSize = ChooseMultiChordSize(hitCount, available, config.KeyCount);
        int slotCount = Math.Max(1, (int)Math.Ceiling(hitCount / (double)chordSize));
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

            int count = Math.Min(chordSize, remaining);
            for (int i = 0; i < count; i++)
            {
                // multi 姿态启发式：和弦内交替空/地（近似原始 MultiLanes 的混合姿态），具体轨道在阶段2分配。
                LanePosture posture = (slot + i) % 2 == 0 ? LanePosture.Ground : LanePosture.Air;
                keys.Add(new KeyDesc(time, time, posture, OsuPlayObjectKind.Multi, slot));
            }

            remaining -= count;
            slot++;
        }
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

    private static void EnsureMusicCollected(List<KeyDesc> keys, NoteInfo note)
    {
        LanePosture target = note.IsAir ? LanePosture.Air : LanePosture.Ground;
        if (IsPostureSatisfied(keys, note.TimeSec, target, MusicWindowSec))
            return;

        keys.Add(new KeyDesc(note.TimeSec, note.TimeSec, target, OsuPlayObjectKind.UtilityTap));
    }

    private static void EnsureBlockDodged(List<KeyDesc> keys, NoteInfo note)
    {
        LanePosture unsafePosture = note.IsAir ? LanePosture.Air : LanePosture.Ground;
        LanePosture safePosture = unsafePosture == LanePosture.Air ? LanePosture.Ground : LanePosture.Air;

        if (IsPostureSatisfied(keys, note.TimeSec, safePosture, BlockWindowSec))
            return;
        if (!IsPostureUnsafe(keys, note.TimeSec, unsafePosture, BlockWindowSec))
            return;

        keys.Add(new KeyDesc(note.TimeSec, note.TimeSec, safePosture, OsuPlayObjectKind.UtilityTap));
    }

    // 齿轮落地修复（阶段1版）：只插入键（姿态），不分配轨道。
    // 原理：在危险窗口 [GearRiskLowSec, GearRiskHighSec] 内显式插入"提前落地"（地面）+
    // "重新起跳"（空中，放在齿轮同时），让落地/起跳由显式事件决定。
    private static void PlanGearLandings(
        List<KeyDesc> keys,
        IReadOnlyList<NoteInfo> notes,
        IReadOnlyList<NoteInfo> multiNotes)
    {
        // 地面点击的时刻（按毫秒分组），用于识别"天地双押"：同一时刻既有空中键又有地面键，
        // 结果是地面状态而非起跳。
        var groundTapMs = new HashSet<int>(keys
            .Where(k => !k.IsHold && k.Posture == LanePosture.Ground)
            .Select(k => (int)Math.Round(k.StartSec * 1000f)));

        // 起跳事件 = 所有空中 tap，排除与地面键同刻的（天地双押）。
        var jumps = keys
            .Where(k => !k.IsHold && k.Posture == LanePosture.Air)
            .Where(k => !groundTapMs.Contains((int)Math.Round(k.StartSec * 1000f)))
            .Select(k => k.StartSec)
            .OrderBy(t => t)
            .ToList();
        int jumpIndex = 0;

        // "必须保持空中"的对象时刻（地面齿轮 + 空中 music），用于计算落地键下界 Tl：
        // 落地键不能早于这些对象，否则会把还在靠当前滞空收集/躲避的对象落地丢掉。
        var airRequirements = notes
            .Where(n => (n.Type == 2 && !n.IsAir) || (n.Type == 7 && n.IsAir))
            .Select(n => n.TimeSec)
            .OrderBy(t => t)
            .ToList();

        float currentJump = float.NegativeInfinity;

        foreach (var gear in notes.Where(n => n.Type == 2 && !n.IsAir).OrderBy(n => n.TimeSec))
        {
            if (IsInsideMulti(gear, multiNotes))
                continue;

            float t = gear.TimeSec;

            // 吸收早于/等于 t 的起跳事件。只有"已落地"（距上次起跳 >= 动画时长）的空键
            // 才是真正的新起跳；滞空中的空键是 no-op（不能二段跳），不推进 currentJump。
            while (jumpIndex < jumps.Count && jumps[jumpIndex] <= t)
            {
                float j = jumps[jumpIndex];
                if (currentJump < 0f || j - currentJump >= AirborneAnimSec)
                    currentJump = j;
                jumpIndex++;
            }

            if (currentJump < 0f)
            {
                // 首个齿轮：EnsureBlockDodged 会在 t 处插入空中键起跳，这里预判推进。
                currentJump = t;
                continue;
            }

            float gap = t - currentJump;
            if (gap < GearRiskLowSec)
                continue;      // 已被当前跳跃安全覆盖
            if (gap > GearRiskHighSec)
            {
                // 已自然落地：EnsureBlockDodged 会在 t 处插入空中键起跳，预判推进。
                currentJump = t;
                continue;
            }

            // 危险窗口：最后一个"必须保持空中"的对象（无则取 currentJump）
            float tl = currentJump;
            foreach (float req in airRequirements)
            {
                if (req >= currentJump && req < t)
                    tl = req;
            }

            float tg = (tl + t) * 0.5f;   // 提前落地（中点，落在 (Tl, t) 内）
            float tj = t;                  // 重新起跳 = block 同时按空，无提前量

            // 保证落地键与起跳键至少相差 1ms，避免 PostureAt 按毫秒分组时 ground 把 air 吞掉
            if (tj - tg < 0.001f)
                tg = tj - 0.001f;

            keys.Add(new KeyDesc(tg, tg, LanePosture.Ground, OsuPlayObjectKind.UtilityTap));
            keys.Add(new KeyDesc(tj, tj, LanePosture.Air, OsuPlayObjectKind.UtilityTap));

            currentJump = tj;
        }
    }

    // ===== 阶段2：根据空地键集合分配轨道 =====

    private static IReadOnlyList<OsuPlayObject> AssignLanes(List<KeyDesc> keys, PlayerConfig config)
    {
        var objects = new List<OsuPlayObject>();
        var scheduler = new RuntimeLaneScheduler(objects, config);

        foreach (var key in keys)
        {
            int lane;
            if (key.Kind == OsuPlayObjectKind.Multi)
            {
                int[] lanes = scheduler.ChooseMultiLanes(key.StartSec, 1, key.MultiSlot);
                if (lanes.Length == 0)
                    continue;
                lane = lanes[0];
            }
            else
            {
                lane = scheduler.ChooseLane(key.StartSec, key.EndSec, key.Posture);
            }

            scheduler.Add(lane, key.StartSec, key.EndSec, key.Kind);
        }

        if (config.EnableLocalSwapOptimizer)
            LocalSwapOptimizer.Optimize(objects, config);

        objects.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        RemoveExactDuplicates(objects);
        return objects;
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

        internal int ChooseLane(float startSec, float endSec, LanePosture posture)
        {
            int[] lanes = _config.LaneIndexesFor(posture);
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
