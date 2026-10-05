using DshPet.Core.Configuration;
using Xunit;

namespace DshPet.Core.Tests;

public class PetStateTests
{
    /// <summary>
    /// The twelve settings these tests care about, with capture mode and the expression
    /// switch left off: every one of them would otherwise have to spell out two more
    /// arguments that the test says nothing about.
    /// </summary>
    static PetState Make(double cm, int pollMs, bool mirror, string source, string window,
                         bool sound, int volume, bool click, bool carousel, int carouselSeconds,
                         int warnPercent, double warnCny) =>
        new PetState(cm, pollMs, mirror, source, window, sound, volume, click, carousel,
                     carouselSeconds, warnPercent, warnCny, false, false, "auto");

    [Fact]
    public void Parses_a_complete_file()
    {
        var state = PetState.Parse("cm=4.5\r\npoll_ms=5000\r\npos=right\r\nsrc=go\r\nwin=month\r\n");
        Assert.Equal(4.5, state.Cm, 6);
        Assert.Equal(5000, state.PollMs);
        Assert.True(state.Mirror);
        Assert.True(state.IsGo);
        Assert.Equal("month", state.Window);
    }

    [Fact]
    public void Defaults_are_the_documented_ones()
    {
        var state = PetState.Parse(null);
        Assert.Equal(8.0, state.Cm, 6);
        Assert.Equal(2000, state.PollMs);
        Assert.False(state.Mirror);
        Assert.False(state.IsGo);
        Assert.Equal("fiveHour", state.Window);
    }

    [Theory]
    [InlineData("poll_ms=999")]        // below the floor
    [InlineData("poll_ms=600001")]     // above the ceiling
    [InlineData("poll_ms=abc")]
    [InlineData("poll_ms=")]
    public void Out_of_range_poll_intervals_are_ignored(string line)
    {
        Assert.Equal(2000, PetState.Parse(line).PollMs);
    }

    [Theory]
    [InlineData("cm=0.5")]             // below the floor
    [InlineData("cm=-3")]
    [InlineData("cm=tiny")]
    public void Out_of_range_sizes_are_ignored(string line)
    {
        Assert.Equal(8.0, PetState.Parse(line).Cm, 6);
    }

    [Fact]
    public void Unknown_keys_and_junk_lines_are_ignored()
    {
        var state = PetState.Parse("nonsense\r\nnoequals\r\nunknown=1\r\n\r\ncm=6\r\n");
        Assert.Equal(6.0, state.Cm, 6);
        Assert.Equal(2000, state.PollMs);
    }

    [Theory]
    [InlineData("src=go", true)]
    [InlineData("src=dsh", false)]
    [InlineData("src=GO", false)]      // only the exact token counts
    [InlineData("src=", false)]
    public void Source_token_maps_to_the_right_mode(string line, bool isGo)
    {
        Assert.Equal(isGo, PetState.Parse(line).IsGo);
    }

    [Theory]
    [InlineData("win=fiveHour", "fiveHour")]
    [InlineData("win=week", "week")]
    [InlineData("win=month", "month")]
    [InlineData("win=year", "fiveHour")]   // unknown falls back to the default
    public void Window_token_is_validated(string line, string expected)
    {
        Assert.Equal(expected, PetState.Parse(line).Window);
    }

    [Fact]
    public void Serialize_then_parse_returns_the_same_state()
    {
        var original = Make(3.5, 7500, true, PetState.SourceGo, "week", false, 35, true, true, 5, 15, 5.0);
        Assert.Equal(original, PetState.Parse(original.Serialize()));
    }

    [Fact]
    public void Serialize_writes_the_keys_the_old_script_wrote()
    {
        string text = Make(8, 2000, false, PetState.SourceDeepSeek, "fiveHour", true, 80, false, false, 5, 15, 5.0).Serialize();
        Assert.Contains("cm=8", text);
        Assert.Contains("poll_ms=2000", text);
        Assert.Contains("pos=left", text);
        Assert.Contains("src=dsh", text);
        Assert.Contains("win=fiveHour", text);
        Assert.Contains("sound=1", text);
        Assert.Contains("volume=80", text);
        Assert.Contains("click=0", text);
        Assert.Contains("carousel=0", text);
    }

    [Theory]
    [InlineData("sound=0", false)]
    [InlineData("sound=1", true)]
    [InlineData("sound=false", true)]      // only the exact "0" token mutes
    public void Sound_flag_is_read(string line, bool expected)
    {
        Assert.Equal(expected, PetState.Parse(line).SoundEnabled);
    }

    [Theory]
    [InlineData("volume=0", 0)]
    [InlineData("volume=55", 55)]
    [InlineData("volume=100", 100)]
    [InlineData("volume=101", 80)]         // out of range, keeps the default
    [InlineData("volume=-1", 80)]
    [InlineData("volume=loud", 80)]
    public void Volume_is_range_checked(string line, int expected)
    {
        Assert.Equal(expected, PetState.Parse(line).Volume);
    }

    [Fact]
    public void A_fallback_supplies_every_key_the_file_does_not_carry()
    {
        // This is how environment variables act as initial defaults: state.ini
        // only overrides the keys it actually contains.
        var fromEnvironment = Make(2.0, 9000, true, PetState.SourceGo, "month", false, 25, true, true, 5, 15, 5.0);
        var state = PetState.Parse("cm=6\r\n", fromEnvironment);

        Assert.Equal(6.0, state.Cm, 6);                      // from the file
        Assert.Equal(9000, state.PollMs);                    // from the fallback
        Assert.True(state.Mirror);
        Assert.False(state.SoundEnabled);
        Assert.Equal(25, state.Volume);
    }

    [Fact]
    public void A_file_without_sound_keys_does_not_reset_them_to_the_built_in_defaults()
    {
        var fallback = PetState.Default with { SoundEnabled = false, Volume = 10 };
        var state = PetState.Parse("cm=8\r\npoll_ms=2000\r\n", fallback);
        Assert.False(state.SoundEnabled);
        Assert.Equal(10, state.Volume);
    }

    [Fact]
    public void Later_lines_win_when_a_key_repeats()
    {
        Assert.Equal(3000, PetState.Parse("poll_ms=2000\r\npoll_ms=3000").PollMs);
    }

    [Fact]
    public void Load_returns_defaults_for_a_missing_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dshpet-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(PetState.Default, PetState.Load(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Save_and_load_round_trip_on_disk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "dshpet-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var state = Make(1.5, 1000, true, PetState.SourceGo, "month", false, 45, true, true, 5, 15, 5.0);
            state.Save(dir);
            Assert.Equal(state, PetState.Load(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Load_falls_back_when_the_file_has_no_sound_keys_yet()
    {
        // An existing v10 state.ini has no sound= / volume= lines; the environment
        // defaults must survive that upgrade.
        string dir = Path.Combine(Path.GetTempPath(), "dshpet-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "state.ini"), "cm=6\r\npoll_ms=3000\r\npos=right\r\nsrc=go\r\nwin=week\r\n");
            var fallback = PetState.Default with { SoundEnabled = false, Volume = 15 };
            var loaded = PetState.Load(dir, fallback);

            Assert.Equal(6.0, loaded.Cm, 6);       // from the file
            Assert.Equal(3000, loaded.PollMs);
            Assert.True(loaded.Mirror);
            Assert.True(loaded.IsGo);
            Assert.Equal("week", loaded.Window);
            Assert.False(loaded.SoundEnabled);     // from the fallback
            Assert.Equal(15, loaded.Volume);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_carousel_is_off_unless_it_was_switched_on()
    {
        // Off by default: quietly changing which number is on screen is not
        // something a widget should start doing on its own.
        Assert.False(PetState.Default.Carousel);
        Assert.False(PetState.Parse("cm=8").Carousel);
        Assert.False(PetState.Parse("carousel=0").Carousel);
        Assert.True(PetState.Parse("carousel=1").Carousel);
    }

    [Fact]
    public void The_carousel_survives_a_round_trip()
    {
        var on = Make(8, 2000, false, PetState.SourceGo, "fiveHour", true, 80, false, true, 5, 15, 5.0);
        Assert.True(PetState.Parse(on.Serialize()).Carousel);
        Assert.Contains("carousel=1", on.Serialize());
        Assert.False(PetState.Parse((on with { Carousel = false }).Serialize()).Carousel);
    }

    [Fact]
    public void Click_through_is_off_unless_it_was_switched_on()
    {
        // Off by default on purpose: a pet that ignores clicks until you already
        // know about Ctrl is a pet that looks broken.
        Assert.False(PetState.Default.ClickThrough);
        Assert.False(PetState.Parse("cm=8").ClickThrough);
        Assert.False(PetState.Parse("click=0").ClickThrough);
        Assert.True(PetState.Parse("click=1").ClickThrough);
        Assert.True(PetState.Parse("click=yes").ClickThrough);   // anything but "0" reads as on
    }

    [Fact]
    public void Click_through_survives_a_round_trip()
    {
        var on = Make(8, 2000, false, PetState.SourceDeepSeek, "fiveHour", true, 80, true, true, 5, 15, 5.0);
        Assert.True(PetState.Parse(on.Serialize()).ClickThrough);
        Assert.Contains("click=1", on.Serialize());

        var off = on with { ClickThrough = false };
        Assert.False(PetState.Parse(off.Serialize()).ClickThrough);
        Assert.Contains("click=0", off.Serialize());
    }

    [Fact]
    public void Capture_mode_is_one_switch_and_survives_a_restart()
    {
        // One switch, with nothing hanging off it: capture mode only changes what kind of
        // window the pet presents itself as, never how it is drawn.
        Assert.False(PetState.Default.ObsMode);
        Assert.False(PetState.Parse("cm=8").ObsMode);
        Assert.False(PetState.Parse("obs_mode=0").ObsMode);
        Assert.True(PetState.Parse("obs_mode=1").ObsMode);
        Assert.True(PetState.Parse("obs_mode=yes").ObsMode);   // anything but "0" reads as on

        var on = PetState.Default with { ObsMode = true };
        Assert.True(PetState.Parse(on.Serialize()).ObsMode);
        Assert.Contains("obs_mode=1", on.Serialize());
        Assert.Contains("obs_mode=0", (on with { ObsMode = false }).Serialize());
    }

    [Fact]
    public void A_leftover_background_colour_from_the_old_key_mode_is_ignored()
    {
        // The colour-key flavour is gone (capture mode keeps the per-pixel alpha), so a
        // state.ini written by that build must simply lose the line - not fail to load,
        // and not resurrect the mode.
        PetState back = PetState.Parse("obs_mode=1\r\nobs_key=00FF00\r\ncm=6.5");
        Assert.True(back.ObsMode);
        Assert.Equal(6.5, back.Cm);
        Assert.DoesNotContain("obs_key", back.Serialize());
    }

    [Fact]
    public void The_theme_defaults_to_following_Windows_and_survives_a_restart()
    {
        // "auto" is the default because the widget should look right without being told;
        // the other two exist for people who keep Windows light but want the widget dark.
        Assert.Equal("auto", PetState.Default.Theme);
        Assert.Equal("auto", PetState.Parse("cm=8").Theme);
        Assert.Equal("dark", PetState.Parse("theme=dark").Theme);
        Assert.Equal("light", PetState.Parse("theme=light").Theme);
        Assert.Equal("dark", PetState.Parse("theme=DARK").Theme);   // case does not matter

        // A typo is not a theme: the default stands rather than the window coming up
        // unstyled. That is why the parser matches three words instead of passing anything
        // through - and why the settings window only ever offers those three.
        Assert.Equal("auto", PetState.Parse("theme=midnight").Theme);
        Assert.Equal("auto", PetState.Parse("theme=").Theme);

        var dark = PetState.Default with { Theme = "dark" };
        Assert.Equal("dark", PetState.Parse(dark.Serialize()).Theme);
        Assert.Contains("theme=dark", dark.Serialize());
    }
}
