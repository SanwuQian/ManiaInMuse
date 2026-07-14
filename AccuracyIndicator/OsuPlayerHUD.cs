using System.Globalization;
using Il2CppInterop.Runtime.Attributes;
using Object = UnityEngine.Object;

namespace AccuracyIndicator;

[RegisterTypeInIl2Cpp]
public class OsuPlayerHUD : MonoBehaviour
{
    private const int MaxVisibleNotes = 256;
    private const int PlayerSortingOrder = 900;
    private const float JudgementLineHeight = 4f;

    private readonly List<OsuPlayObject> _objects = new();
    private readonly List<NoteVisual> _visuals = new();
    private RectTransform _playfield;
    private PlayerConfig _config;
    private bool _dead;
    private bool _loggedMissingFile;

    public OsuPlayerHUD(IntPtr ptr) : base(ptr) { }

    private void Start()
    {
        _config = PlayerConfig.LoadOrCreate();

        var canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = PlayerSortingOrder;

        var scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);

        BuildPlayfield();
        if (_objects.Count == 0)
            LoadLatestOsu();
    }

    private void BuildPlayfield()
    {
        var playfieldGo = new GameObject("Playfield");
        playfieldGo.transform.SetParent(gameObject.transform, false);

        _playfield = playfieldGo.AddComponent<RectTransform>();
        _playfield.anchorMin = new Vector2(0.5f, 0.5f);
        _playfield.anchorMax = new Vector2(0.5f, 0.5f);
        _playfield.pivot = new Vector2(0.5f, 0.5f);
        _playfield.anchoredPosition = new Vector2(_config.PositionX, _config.PositionY);
        _playfield.sizeDelta = new Vector2(_config.TrackWidth, _config.TrackHeight);

        var background = playfieldGo.AddComponent<Image>();
        background.color = _config.BackgroundColor;
        background.raycastTarget = false;

        playfieldGo.AddComponent<RectMask2D>();

        var lineGo = new GameObject("JudgementLine");
        lineGo.transform.SetParent(_playfield, false);
        var lineRect = lineGo.AddComponent<RectTransform>();
        lineRect.anchorMin = new Vector2(0.5f, 0.5f);
        lineRect.anchorMax = new Vector2(0.5f, 0.5f);
        lineRect.pivot = new Vector2(0.5f, 0.5f);
        lineRect.anchoredPosition = new Vector2(0, JudgementLineY(_config));
        lineRect.sizeDelta = new Vector2(_config.TrackWidth, JudgementLineHeight);
        var line = lineGo.AddComponent<Image>();
        line.color = Color.white;
        line.raycastTarget = false;

        for (int i = 0; i < MaxVisibleNotes; i++)
            _visuals.Add(new NoteVisual(_playfield, _config.NoteColor, _config.HoldColor));
    }

    [HideFromIl2Cpp]
    internal void LoadObjects(IReadOnlyList<OsuPlayObject> objects)
    {
        _objects.Clear();
        foreach (var obj in objects)
            _objects.Add(obj);

        _objects.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        HideAll();
        MelonLogger.Msg($"[ManiaInMuse] osu player refreshed {_objects.Count} objects");
    }

    private void LoadLatestOsu()
    {
        _objects.Clear();
        string path = Path.Combine("UserData", "ManiaInMuse", "maps", "latest.osu");
        if (!File.Exists(path))
        {
            if (!_loggedMissingFile)
            {
                _loggedMissingFile = true;
                MelonLogger.Warning($"[ManiaInMuse] osu player could not find {path}");
            }
            return;
        }

        try
        {
            foreach (var obj in OsuPlayObjectReader.Read(path, _config))
                _objects.Add(obj);

            MelonLogger.Msg($"[ManiaInMuse] osu player loaded {_objects.Count} objects from {path}");
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[ManiaInMuse] osu player failed to load {path}: {ex}");
        }
    }

    private void Update()
    {
        if (_dead || _playfield == null)
            return;

        if (!Main.Active)
        {
            HideAll();
            return;
        }

        Main.UpdatePlaybackState();
        if (!Main.ShouldShowPlayerHud())
        {
            SetPlayfieldVisible(false);
            HideAll();
            return;
        }

        SetPlayfieldVisible(true);
        Render(Main.SongTime);
    }

    private void Render(float songTime)
    {
        int visualIndex = 0;
        float trackTopY = _config.TrackHeight * 0.5f;
        float trackBottomY = -_config.TrackHeight * 0.5f;
        float spawnY = trackTopY + _config.NoteHeight * 0.5f;
        float judgementY = JudgementLineY(_config) + _config.NoteHeight * 0.5f;

        for (int i = 0; i < _objects.Count && visualIndex < _visuals.Count; i++)
        {
            var obj = _objects[i];
            if (obj.EndSec < songTime - 0.1f)
                continue;
            if (obj.StartSec > songTime + _config.FallTimeSec)
                break;

            float headY = YForTime(obj.StartSec, songTime, spawnY, judgementY, _config.FallTimeSec);
            float tailY = obj.IsHold ? YForTime(obj.EndSec, songTime, spawnY, judgementY, _config.FallTimeSec) : headY;

            bool headVisible = headY >= trackBottomY - _config.NoteHeight && headY <= trackTopY + _config.NoteHeight;
            bool bodyVisible = obj.IsHold && Math.Max(headY, tailY) >= trackBottomY && Math.Min(headY, tailY) <= trackTopY;
            if (!headVisible && !bodyVisible)
                continue;

            _visuals[visualIndex++].Show(obj, headY, tailY, trackBottomY, trackTopY, _config, _config.NoteWidth, _config.NoteHeight);
        }

        for (int i = visualIndex; i < _visuals.Count; i++)
            _visuals[i].Hide();
    }

    private static float YForTime(float objectTime, float songTime, float topY, float bottomY, float fallTimeSec)
    {
        float untilHit = objectTime - songTime;
        float progress = 1f - untilHit / fallTimeSec;
        return Mathf.Lerp(topY, bottomY, progress);
    }

    private static float JudgementLineY(PlayerConfig config)
    {
        return config.TrackHeight * (0.5f - config.JudgementLinePosition);
    }

    private void HideAll()
    {
        foreach (var visual in _visuals)
            visual.Hide();
    }

    private void SetPlayfieldVisible(bool visible)
    {
        if (_playfield != null && _playfield.gameObject.activeSelf != visible)
            _playfield.gameObject.SetActive(visible);
    }

    private void OnDestroy()
    {
        _dead = true;
        _objects.Clear();
        HideAll();

        if (Main.PlayerHUD == this)
            Main.PlayerHUD = null;
    }

    private sealed class NoteVisual
    {
        private readonly RectTransform _headRect;
        private readonly RectTransform _bodyRect;
        private readonly Image _head;
        private readonly Image _body;
        private readonly GameObject _headGo;
        private readonly GameObject _bodyGo;

        internal NoteVisual(RectTransform parent, Color32 noteColor, Color32 holdColor)
        {
            _bodyGo = new GameObject("HoldBody");
            _bodyGo.transform.SetParent(parent, false);
            _bodyRect = _bodyGo.AddComponent<RectTransform>();
            _body = _bodyGo.AddComponent<Image>();
            _body.color = holdColor;
            _body.raycastTarget = false;

            _headGo = new GameObject("Head");
            _headGo.transform.SetParent(parent, false);
            _headRect = _headGo.AddComponent<RectTransform>();
            _head = _headGo.AddComponent<Image>();
            _head.color = noteColor;
            _head.raycastTarget = false;

            Hide();
        }

        internal void Show(OsuPlayObject obj, float headY, float tailY, float bottomY, float topY, PlayerConfig config, float noteWidth, float noteHeight)
        {
            float x = config.LaneToPlayfieldX(obj.Lane, config.TrackWidth);

            if (obj.IsHold)
            {
                float lowerY = Mathf.Clamp(Math.Min(headY, tailY), bottomY, topY);
                float upperY = Mathf.Clamp(Math.Max(headY, tailY), bottomY, topY);
                float bodyHeight = Math.Max(0, upperY - lowerY);

                _bodyGo.SetActive(bodyHeight > 1);
                _bodyRect.anchorMin = new Vector2(0.5f, 0.5f);
                _bodyRect.anchorMax = new Vector2(0.5f, 0.5f);
                _bodyRect.pivot = new Vector2(0.5f, 0.5f);
                _bodyRect.anchoredPosition = new Vector2(x, (lowerY + upperY) * 0.5f);
                _bodyRect.sizeDelta = new Vector2(noteWidth, bodyHeight);
            }
            else
            {
                _bodyGo.SetActive(false);
            }

            bool headVisible = headY >= bottomY - noteHeight && headY <= topY + noteHeight;
            _headGo.SetActive(headVisible);
            if (headVisible)
            {
                _headRect.anchorMin = new Vector2(0.5f, 0.5f);
                _headRect.anchorMax = new Vector2(0.5f, 0.5f);
                _headRect.pivot = new Vector2(0.5f, 0.5f);
                _headRect.anchoredPosition = new Vector2(x, headY);
                _headRect.sizeDelta = new Vector2(noteWidth, noteHeight);
            }
        }

        internal void Hide()
        {
            _headGo.SetActive(false);
            _bodyGo.SetActive(false);
        }
    }
}

internal readonly struct OsuPlayObject
{
    internal readonly int Lane;
    internal readonly float StartSec;
    internal readonly float EndSec;
    internal readonly bool IsHold;
    internal readonly OsuPlayObjectKind Kind;

    internal OsuPlayObject(int lane, float startSec, float endSec, bool isHold, OsuPlayObjectKind kind = OsuPlayObjectKind.RegularTap)
    {
        Lane = lane;
        StartSec = startSec;
        EndSec = endSec;
        IsHold = isHold;
        Kind = kind;
    }

    internal bool IsLocalSwapCandidate => !IsHold && (Kind is OsuPlayObjectKind.RegularTap or OsuPlayObjectKind.BossTap);
    internal bool AllowsAnyPosture => Kind == OsuPlayObjectKind.BossTap;

    internal OsuPlayObject WithLane(int lane)
    {
        return new OsuPlayObject(lane, StartSec, EndSec, IsHold, Kind);
    }
}

internal enum OsuPlayObjectKind
{
    RegularTap,
    BossTap,
    Hold,
    Multi,
    UtilityTap,
    Imported
}

internal static class OsuPlayObjectReader
{
    internal static IEnumerable<OsuPlayObject> Read(string path, PlayerConfig config)
    {
        bool inHitObjects = false;
        var objects = new List<OsuPlayObject>();

        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
        {
            while (reader.ReadLine() is { } line)
            {
                line = line.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inHitObjects = line.Equals("[HitObjects]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inHitObjects)
                    continue;

                var obj = ParseHitObject(line, config);
                if (obj.HasValue)
                    objects.Add(obj.Value);
            }
        }

        objects.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        return objects;
    }

    private static OsuPlayObject? ParseHitObject(string line, PlayerConfig config)
    {
        string[] parts = line.Split(',');
        if (parts.Length < 5)
            return null;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x))
            return null;
        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int startMs))
            return null;
        if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int type))
            return null;

        int lane = config.XToLane(x);
        bool isHold = (type & 128) != 0;
        int endMs = startMs;
        if (isHold && parts.Length >= 6)
        {
            string endText = parts[5].Split(':')[0];
            if (!int.TryParse(endText, NumberStyles.Integer, CultureInfo.InvariantCulture, out endMs))
                endMs = startMs;
        }

        return new OsuPlayObject(lane, startMs / 1000f, Math.Max(startMs, endMs) / 1000f, isHold, OsuPlayObjectKind.Imported);
    }
}
