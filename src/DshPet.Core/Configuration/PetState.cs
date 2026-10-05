using System.Globalization;
using System.Text;

namespace DshPet.Core.Configuration;

/// <summary>
/// Everything the widget remembers between runs, persisted as <c>state.ini</c>.
///
/// Unknown keys and out-of-range values are ignored rather than fatal: the file
/// is user-editable, and a bad line should never stop the pet from starting.
///
/// Every value here is also settable from the settings window, which is why
/// sound and volume live in this file rather than only in environment
/// variables. Environment variables still work, but as the *initial* defaults:
/// once a key exists in state.ini it wins, so a GUI change is never silently
/// undone by a stale variable.
/// </summary>
public sealed record PetState(
    double Cm,
    int PollMs,
    bool Mirror,
    string Source,
    string Window,
    bool SoundEnabled,
    int Volume,
    bool ClickThrough,
    bool Carousel,
    int CarouselSeconds,
    int WarnPercent,
    double WarnCny,
    bool ObsMode,
    bool Expressions,
    string Theme)
{
    public const string SourceDeepSeek = "dsh";
    public const string SourceGo = "go";

    public static PetState Default { get; } = new(8.0, 2000, false, SourceDeepSeek, "fiveHour", true, 80,
                                                   false, false, 5, 15, 5.0, false, true, "auto");

    public bool IsGo => Source == SourceGo;

    public static PetState Parse(string? ini, PetState? fallback = null)
    {
        PetState state = fallback ?? Default;
        if (string.IsNullOrWhiteSpace(ini)) return state;

        foreach (string raw in ini!.Split('\n'))   // net48 refs lack [NotNullWhen]
        {
            string line = raw.Trim();
            int i = line.IndexOf('=');
            if (i <= 0) continue;
            string key = line.Substring(0, i).Trim();          // no range syntax on net48
            string value = line.Substring(i + 1).Trim();

            switch (key)
            {
                case "pos":
                    state = state with { Mirror = value == "right" };
                    break;
                case "poll_ms" when int.TryParse(value, out int ms) && ms >= 1000 && ms <= 600_000:
                    state = state with { PollMs = ms };
                    break;
                case "cm" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double cm)
                                  && cm >= 0.8:
                    state = state with { Cm = cm };
                    break;
                case "src":
                    state = state with { Source = value == SourceGo ? SourceGo : SourceDeepSeek };
                    break;
                case "win" when value is "fiveHour" or "week" or "month":
                    state = state with { Window = value };
                    break;
                case "sound":
                    state = state with { SoundEnabled = value != "0" };
                    break;
                case "volume" when int.TryParse(value, out int volume) && volume >= 0 && volume <= 100:
                    state = state with { Volume = volume };
                    break;
                case "click":
                    state = state with { ClickThrough = value != "0" };
                    break;
                case "carousel":
                    state = state with { Carousel = value != "0" };
                    break;
                case "carousel_s" when int.TryParse(value, out int secs) && secs >= 2 && secs <= 3600:
                    state = state with { CarouselSeconds = secs };
                    break;
                // 0 disables the warning entirely, which is a real choice: it is the
                // only way to say "tell me when it runs out, never before".
                case "warn_pct" when int.TryParse(value, out int pct) && pct >= 0 && pct <= 100:
                    state = state with { WarnPercent = pct };
                    break;
                case "warn_cny" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                                                     out double cny) && cny >= 0 && cny <= 1_000_000:
                    state = state with { WarnCny = cny };
                    break;
                // Capture mode: present the window as an ordinary application window so
                // a capturer can see it. One switch, nothing else - the renderer, the
                // size and the background are identical either way.
                case "obs_mode":
                    state = state with { ObsMode = value != "0" };
                    break;
                // A different face for each charge, from expressions\. On by default:
                // the folder ships with the widget, and without it the setting does
                // nothing anyway.
                case "expr":
                    state = state with { Expressions = value != "0" };
                    break;
                // auto | dark | light. Anything else is a typo, not a theme, so the
                // default stands rather than the window coming up unstyled.
                case "theme": {
                    string theme = value.ToLowerInvariant();
                    if (theme == "auto" || theme == "dark" || theme == "light")
                        state = state with { Theme = theme };
                    break;
                }
            }
        }
        return state;
    }

    public string Serialize() =>
        "cm=" + Cm.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n" +
        "poll_ms=" + PollMs.ToString(CultureInfo.InvariantCulture) + "\r\n" +
        "pos=" + (Mirror ? "right" : "left") + "\r\n" +
        "src=" + Source + "\r\n" +
        "win=" + Window + "\r\n" +
        "sound=" + (SoundEnabled ? "1" : "0") + "\r\n" +
        "volume=" + Volume.ToString(CultureInfo.InvariantCulture) + "\r\n" +
        "click=" + (ClickThrough ? "1" : "0") + "\r\n" +
        "carousel=" + (Carousel ? "1" : "0") + "\r\n" +
        "carousel_s=" + CarouselSeconds.ToString(CultureInfo.InvariantCulture) + "\r\n" +
        "warn_pct=" + WarnPercent.ToString(CultureInfo.InvariantCulture) + "\r\n" +
        "warn_cny=" + WarnCny.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n" +
        "obs_mode=" + (ObsMode ? "1" : "0") + "\r\n" +
        "expr=" + (Expressions ? "1" : "0") + "\r\n" +
        "theme=" + Theme + "\r\n";

    /// <summary>
    /// Reads state.ini, using <paramref name="fallback"/> (normally built from
    /// the environment) for every key the file does not carry.
    /// </summary>
    public static PetState Load(string directory, PetState? fallback = null)
    {
        PetState baseline = fallback ?? Default;
        string path = Path.Combine(directory, "state.ini");
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path), baseline) : baseline;
        }
        catch (IOException) { return baseline; }
        catch (UnauthorizedAccessException) { return baseline; }
    }

    public void Save(string directory)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, "state.ini"), Serialize(), Encoding.ASCII);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
