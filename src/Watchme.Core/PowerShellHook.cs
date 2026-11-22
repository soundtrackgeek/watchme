using System.Text;

namespace Watchme.Core;

/// <summary>
/// The PowerShell side of command logging: a hook script that records every command you run
/// as one JSON line in <see cref="Paths.Inbox"/>, and the block in your profile that loads it.
/// Only PowerShell 7 (pwsh) is set up; its profile is shared by Windows Terminal and the console.
/// </summary>
public static class PowerShellHook
{
    public const string BeginMarker = "# >>> watchme >>>";
    public const string EndMarker = "# <<< watchme <<<";

    public static string ScriptPath => Path.Combine(Paths.Hooks, "powershell.ps1");

    /// <summary>pwsh's profile for the current user and host (Documents follows OneDrive's redirection).</summary>
    public static string DefaultProfilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PowerShell", "Microsoft.PowerShell_profile.ps1");

    /// <summary>
    /// The hook, ASCII only so any profile encoding can load it. It wraps the prompt function,
    /// which PowerShell calls right after each command: $? and $LASTEXITCODE still describe that
    /// command when the wrapper starts, and Get-History has its text and timing.
    /// Commands that start with a space, or mention passwords, tokens and the like, are stored
    /// as "[hidden]". Every error is swallowed: logging must never break your prompt.
    /// </summary>
    public static string Script(string inbox) => $$"""
        # Watchme: records each command you run in PowerShell (text, folder, timing, success)
        # as one JSON line in Watchme's inbox, for the Watchme agent to pick up.
        # Written by 'watchme powershell install'; changes are overwritten.
        if ($global:__watchme) { return }
        $global:__watchme = @{
            Inbox   = '{{inbox.Replace("'", "''")}}'
            Session = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            LastId  = (Get-History -Count 1).Id
            Cwd     = $PWD.ProviderPath
            Prompt  = $function:prompt
            Shell   = "pwsh $($PSVersionTable.PSVersion)"
            Hide    = '(?i)passw|secret|token|apikey|api_key|asplaintext|connectionstring|credential'
        }

        function global:prompt {
            $ok = $global:?
            $code = $global:LASTEXITCODE
            $w = $global:__watchme
            try {
                $h = Get-History -Count 1
                if ($h -and $h.Id -ne $w.LastId) {
                    $w.LastId = $h.Id
                    $cmd = $h.CommandLine
                    if ($cmd -match '^\s' -or $cmd -match $w.Hide) { $cmd = '[hidden]' }
                    $line = [ordered]@{
                        v         = 1
                        session   = $w.Session
                        pid       = $PID
                        id        = $h.Id
                        shell     = $w.Shell
                        start     = ([DateTimeOffset]$h.StartExecutionTime).ToUnixTimeMilliseconds()
                        end       = ([DateTimeOffset]$h.EndExecutionTime).ToUnixTimeMilliseconds()
                        cmd       = $cmd
                        cwd       = $w.Cwd
                        cwd_after = $PWD.ProviderPath
                        ok        = [bool]$ok
                        status    = "$($h.ExecutionStatus)"
                        exit_code = $code
                    } | ConvertTo-Json -Compress
                    $file = Join-Path $w.Inbox ('powershell-' + (Get-Date -Format 'yyyy-MM-dd') + '.jsonl')
                    if (-not (Test-Path -LiteralPath $w.Inbox)) { $null = New-Item -ItemType Directory -Path $w.Inbox -Force }
                    for ($try = 0; $try -lt 3; $try++) {
                        try { [IO.File]::AppendAllText($file, $line + "`n", [Text.UTF8Encoding]::new($false)); break }
                        catch [IO.IOException] { Start-Sleep -Milliseconds 20 }
                    }
                }
                $w.Cwd = $PWD.ProviderPath
            } catch { }
            $global:LASTEXITCODE = $code
            if ($w.Prompt) { & $w.Prompt } else { "PS $($PWD.ProviderPath)> " }
        }

        """.ReplaceLineEndings("\r\n");

    /// <summary>The lines added to the profile; they load the hook only while it exists.</summary>
    public static string ProfileBlock(string scriptPath, string newline = "\r\n") =>
        string.Join(newline,
            BeginMarker,
            $"$__watchmeHook = '{scriptPath.Replace("'", "''")}'; if (Test-Path -LiteralPath $__watchmeHook) {{ . $__watchmeHook }}",
            EndMarker) + newline;

    public static bool HasBlock(string profile) => profile.Contains(BeginMarker, StringComparison.Ordinal);

    /// <summary>
    /// The profile with the block at its end (replacing an existing one). Last, so a prompt set
    /// earlier in the profile (oh-my-posh, Starship...) is the one that gets wrapped.
    /// </summary>
    public static string WithBlock(string profile, string scriptPath)
    {
        var text = WithoutBlock(profile);
        var newline = text.Contains("\r\n", StringComparison.Ordinal) || !text.Contains('\n') ? "\r\n" : "\n";
        if (text.Length > 0 && !text.EndsWith('\n'))
            text += newline;
        return text + ProfileBlock(scriptPath, newline);
    }

    /// <summary>The profile with the block (and the line break after it) removed; the rest is untouched.</summary>
    public static string WithoutBlock(string profile)
    {
        var begin = profile.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (begin < 0)
            return profile;
        var end = profile.IndexOf(EndMarker, begin, StringComparison.Ordinal);
        if (end < 0)
            return profile;
        end += EndMarker.Length;
        if (end < profile.Length && profile[end] == '\r')
            end++;
        if (end < profile.Length && profile[end] == '\n')
            end++;
        return profile[..begin] + profile[end..];
    }

    /// <summary>Reads a profile so that writing it back changes nothing but the block.</summary>
    public static (string Text, Encoding Encoding) ReadProfile(string path)
    {
        var bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
        // UTF-16 profiles are edited as text; anything else (UTF-8 with or without BOM, ANSI) as
        // Latin-1, which maps every byte to one char and back, so foreign bytes survive untouched.
        if (bytes is [0xFF, 0xFE, ..])
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(false, true));
        if (bytes is [0xFE, 0xFF, ..])
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), new UnicodeEncoding(true, true));
        return (Encoding.Latin1.GetString(bytes), Encoding.Latin1);
    }

    public static void WriteProfile(string path, string text, Encoding encoding)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var body = encoding.GetBytes(text);
        File.WriteAllBytes(path, [.. encoding.GetPreamble(), .. body]);
    }
}
