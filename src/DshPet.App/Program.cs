using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DshPet.App;

/// <summary>
/// The entry point. This is the PowerShell glue from the bottom of dsh_pet.ps1
/// moved into C#: locate the API key, point the sprite at this folder, then hand
/// over to the (for now verbatim) legacy widget code.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Everything (sprite, sound, state.ini, pet.log, credentials) lives next
        // to the exe. DSHPET_DIR overrides it, which is how the dev build can be
        // pointed at the repository root during the phase-1 parity checks.
        string here = Environment.GetEnvironmentVariable("DSHPET_DIR") is { Length: > 0 } dir
            ? dir
            : AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // A WinExe has no console of its own, so every --shot / --gosim /
        // --selftest run would print into the void. Give it a usable stdout
        // before anything else touches Console.
        EnsureStdout();

        try
        {
            LoadDeepSeekKey(here);
            if (Environment.GetEnvironmentVariable("DSHPET_SPRITE") is not { Length: > 0 })
                Environment.SetEnvironmentVariable("DSHPET_SPRITE", Path.Combine(here, "sprite.png"));

            Legacy.DshPet.Run(here, args);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(here, "error.log"), ex.ToString()); } catch { }
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>
    /// The DeepSeek key, in the same order the script used: an explicit override,
    /// then apikey.txt next to the app, then DSH's own credential store.
    /// </summary>
    private static void LoadDeepSeekKey(string here)
    {
        if (Environment.GetEnvironmentVariable("DSHPET_KEY") is { Length: > 0 }) return;

        string keyFile = Path.Combine(here, "apikey.txt");
        if (File.Exists(keyFile))
        {
            string key = File.ReadAllText(keyFile).Trim();
            if (key.Length > 0)
            {
                Environment.SetEnvironmentVariable("DSHPET_KEY", key);
                return;
            }
        }

        string cred = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh", ".credentials.yaml");
        if (!File.Exists(cred)) return;
        Match m = Regex.Match(File.ReadAllText(cred), @"DEEPSEEK_API_KEY:\s*(\S+)");
        if (m.Success) Environment.SetEnvironmentVariable("DSHPET_KEY", m.Groups[1].Value);
    }

    // ------------------------------------------------------------------ console --

    private const int StdOutputHandle = -11;
    private const int AttachParentProcess = -1;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private static bool NeedsConsole()
    {
        IntPtr h = GetStdHandle(StdOutputHandle);
        return h == IntPtr.Zero || h == InvalidHandleValue;
    }

    /// <summary>
    /// Makes Console.Out actually go somewhere.
    ///
    /// Two different situations have to work, and they pull in opposite
    /// directions: a terminal run, where the process owns no console and has to
    /// borrow the launcher's, and a redirected run, where a stdout handle is
    /// already there and must not be replaced. Re-wrapping the existing handle is
    /// also what rescues the case where the runtime already cached a null writer.
    /// </summary>
    private static void EnsureStdout()
    {
        bool usable = !NeedsConsole();
        if (!usable) usable = AttachConsole(AttachParentProcess);
        if (!usable) return;                     // double-clicked: nothing to write to

        try
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        catch
        {
            // No stream after all - leave the default writer alone.
        }
    }
}
