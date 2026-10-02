using System.Text.RegularExpressions;

namespace DshPet.Core.Credentials;

/// <summary>
/// DSH's own credential store, <c>%USERPROFILE%\.dsh\.credentials.yaml</c>,
/// where the DeepSeek key already lives if the user has ever run DSH.
///
/// A targeted regex rather than a YAML parser, matching what the PowerShell
/// generation did: the file is written by another tool, only one key is ever
/// wanted from it, and a full YAML dependency for one line would be silly.
/// </summary>
public static class DshCredentials
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dsh", ".credentials.yaml");

    /// <summary>The <c>DEEPSEEK_API_KEY</c> value, or null when there is none.</summary>
    public static string? LoadKey(string? path = null)
    {
        string file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file)) return null;
            Match m = Regex.Match(File.ReadAllText(file), @"DEEPSEEK_API_KEY:\s*(\S+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
