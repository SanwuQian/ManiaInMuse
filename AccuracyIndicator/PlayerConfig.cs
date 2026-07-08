using System.Globalization;

namespace AccuracyIndicator;

internal sealed class PlayerConfig
{
    internal const string ConfigPath = "UserData\\ManiaInMuse\\Player.cfg";

    internal float FallTimeMs { get; private set; } = 348f;
    internal float TrackWidth { get; private set; } = 540f;
    internal float TrackHeight { get; private set; } = 1080f;
    internal float NoteWidth { get; private set; } = 77f;
    internal float NoteHeight { get; private set; } = 28f;
    internal float PositionX { get; private set; }
    internal float PositionY { get; private set; }
    internal Color32 BackgroundColor { get; private set; } = new(0, 0, 0, 255);
    internal Color32 NoteColor { get; private set; } = new(0, 220, 70, 255);
    internal Color32 HoldColor { get; private set; } = new(110, 110, 110, 255);

    internal float FallTimeSec => Math.Max(1f, FallTimeMs) / 1000f;

    internal static PlayerConfig LoadOrCreate()
    {
        EnsureDefaultFile();

        var config = new PlayerConfig();
        if (!File.Exists(ConfigPath))
            return config;

        foreach (string rawLine in File.ReadAllLines(ConfigPath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal))
                continue;

            int separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            config.Apply(key, value);
        }

        config.TrackWidth = Math.Max(60f, config.TrackWidth);
        config.TrackHeight = Math.Max(120f, config.TrackHeight);
        config.NoteWidth = Math.Max(1f, config.NoteWidth);
        config.NoteHeight = Math.Max(1f, config.NoteHeight);
        config.FallTimeMs = Math.Max(1f, config.FallTimeMs);
        return config;
    }

    private void Apply(string key, string value)
    {
        if (TryReadFloat(value, out float number))
        {
            switch (key.ToLowerInvariant())
            {
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
                case "keywidth":
                case "clickwidth":
                    NoteWidth = number;
                    return;
                case "noteheight":
                case "keyheight":
                case "clickheight":
                    NoteHeight = number;
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
            switch (key.ToLowerInvariant())
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

    private static void EnsureDefaultFile()
    {
        try
        {
            string directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (File.Exists(ConfigPath))
                return;

            File.WriteAllText(ConfigPath,
                """
                [Player]
                # Time from note spawn at top to judgement line, in milliseconds.
                FallTimeMs = 348

                # Track rectangle size in 1920x1080 canvas coordinates.
                TrackWidth = 540
                TrackHeight = 1080

                # Click note size. Hold heads use the same size; hold bodies use NoteWidth.
                NoteWidth = 77
                NoteHeight = 28

                # Track center offset from screen center.
                PositionX = 0
                PositionY = 0

                # Colors are R,G,B or R,G,B,A, range 0-255.
                BackgroundColor = 0,0,0,255
                NoteColor = 0,220,70,255
                HoldColor = 110,110,110,255
                """);
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[ManiaTest] Failed to create player config: {ex.Message}");
        }
    }

    private static bool TryReadFloat(string value, out float result)
    {
        return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
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
