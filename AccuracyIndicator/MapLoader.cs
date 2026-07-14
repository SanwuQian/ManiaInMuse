using System.Globalization;
using System.Text;

namespace AccuracyIndicator;

internal static class MapLoader
{
    private const string ExportDirectory = "UserData\\ManiaInMuse\\maps";

    internal static void SaveCurrentMap(IReadOnlyList<NoteInfo> notes, float bpm, float runtimeBpm, PlayerConfig config)
    {
        if (notes.Count == 0)
            return;

        Directory.CreateDirectory(ExportDirectory);

        string fileName = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{notes.Count}_notes.csv";
        string path = Path.Combine(ExportDirectory, fileName);
        string latestPath = Path.Combine(ExportDirectory, "latest.csv");
        string csv = BuildCsv(notes, bpm, runtimeBpm);

        File.WriteAllText(path, csv, Encoding.UTF8);
        File.WriteAllText(latestPath, csv, Encoding.UTF8);

        MelonLogger.Msg($"[ManiaInMuse] Map exported: {path}");

        if (config != null && config.AutoCleanCache)
            CleanExportCache(config.CacheMaxMapFiles);
    }

    private static void CleanExportCache(int maxMapFiles)
    {
        try
        {
            if (maxMapFiles < 0 || !Directory.Exists(ExportDirectory))
                return;

            var files = Directory.GetFiles(ExportDirectory, "*_notes.csv", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            int removed = 0;
            foreach (var file in files.Skip(maxMapFiles))
            {
                try
                {
                    file.Delete();
                    removed++;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[ManiaInMuse] Failed to delete cache file {file.FullName}: {ex.Message}");
                }
            }

            if (removed > 0)
                MelonLogger.Msg($"[ManiaInMuse] Cache cleanup removed {removed} old map export(s)");
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[ManiaInMuse] Cache cleanup failed: {ex.Message}");
        }
    }

    private static string BuildCsv(IReadOnlyList<NoteInfo> notes, float bpm, float runtimeBpm)
    {
        var sb = new StringBuilder();
        sb.Append("# bpm=").Append(ToFloat(bpm)).Append(",runtime_bpm=").Append(ToFloat(runtimeBpm)).AppendLine();
        sb.AppendLine("index,time_sec,time_ms,end_time_sec,end_time_ms,length_sec,length_ms,type,type_name,is_air,multi_low_threshold,multi_mid_threshold,multi_high_threshold,multi_max_hit_count,multi_duration_sec,multi_duration_ms,bpm,runtime_bpm");

        for (int i = 0; i < notes.Count; i++)
        {
            NoteInfo note = notes[i];
            float lengthSec = note.EndTimeSec > note.TimeSec ? note.EndTimeSec - note.TimeSec : 0;
            int timeMs = ToMs(note.TimeSec);
            int endTimeMs = note.EndTimeSec > note.TimeSec ? ToMs(note.EndTimeSec) : 0;
            int lengthMs = ToMs(lengthSec);

            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(ToFloat(note.TimeSec)).Append(',');
            sb.Append(timeMs.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(ToFloat(note.EndTimeSec)).Append(',');
            sb.Append(endTimeMs.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(ToFloat(lengthSec)).Append(',');
            sb.Append(lengthMs.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(note.Type.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(NoteTypeName(note.Type)).Append(',');
            sb.Append(note.IsAir ? "true" : "false").Append(',');
            sb.Append(note.MultiLowThreshold.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(note.MultiMidThreshold.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(note.MultiHighThreshold.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(note.MultiMaxHitCount.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(ToFloat(note.MultiDurationSec)).Append(',');
            sb.Append(ToMs(note.MultiDurationSec).ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(ToFloat(bpm)).Append(',');
            sb.Append(ToFloat(runtimeBpm));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static int ToMs(float seconds)
    {
        return (int)Math.Round(seconds * 1000f, MidpointRounding.AwayFromZero);
    }

    private static string ToFloat(float value)
    {
        return value.ToString("0.000000", CultureInfo.InvariantCulture);
    }

    internal static string NoteTypeName(int type)
    {
        return type switch
        {
            1 => "monster",
            2 => "block",
            3 => "hold",
            4 => "ghost",
            5 => "boss",
            6 => "energy",
            7 => "music",
            8 => "multi",
            _ => "unknown"
        };
    }

}
