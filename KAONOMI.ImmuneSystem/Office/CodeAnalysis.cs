namespace KAONOMI.ImmuneSystem.Office;

using System.Text.RegularExpressions;

/// <summary>What a piece of macro code does: what makes it run by itself, what it can do to the machine, the addresses it names.</summary>
public static class CodeAnalysis
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Multiline;

    private static readonly Regex VbaAutoExec = new(
        @"^\s*(?:(?:Private|Public)\s+)?(?:Sub|Function)\s+(AutoExec|AutoOpen|AutoClose|AutoExit|AutoNew|Auto_Open|Auto_Close|Document_Open|Document_Close|Document_New|Document_BeforeClose|Document_ContentControlOnEnter|DocumentOpen|DocumentBeforeClose|Workbook_Open|Workbook_Activate|Workbook_BeforeClose|Workbook_Deactivate|Workbook_Window\w+|OnSlideShowPageChange|(?!UserForm_)\w+_(?:Painted|Layout|Resize|GotFocus|LostFocus|MouseHover|MouseMove))\b",
        Opt);

    private static readonly (string Label, Regex Pattern)[] VbaSuspicious =
    [
        ("Runs programs (Shell)", new(@"\b(?:Shell|ShellExecute[AW]?)\b|WScript\.Shell|\.Exec\b|Win32_Process|winmgmts", Opt)),
        ("PowerShell / cmd", new(@"powershell|\bcmd(?:\.exe)?\s+/[ck]\b|\bmshta\b|\brundll32\b|\bregsvr32\b|\bcertutil\b|\bbitsadmin\b", Opt)),
        ("Creates COM objects (CreateObject / GetObject)", new(@"\b(?:CreateObject|GetObject)\b", Opt)),
        ("Downloads (XMLHTTP, WinHttp, URLDownloadToFile)", new(@"URLDownloadToFile|XMLHTTP|WinHttp|InternetOpen|InternetReadFile|Msxml2|Net\.WebClient|DownloadFile|DownloadString", Opt)),
        ("Writes files", new(@"ADODB\.Stream|SaveToFile|CreateTextFile|\bOpen\b[^\n]+\bFor\s+(?:Binary|Output|Append)\b|\bPut\s+#|\bPrint\s+#", Opt)),
        ("Deletes files (Kill)", new(@"\bKill\b|DeleteFile|DeleteFolder", Opt)),
        ("Reads the environment (Environ)", new(@"\bEnviron\$?\s*\(", Opt)),
        ("Calls the Windows API (Declare … Lib)", new(@"\bDeclare\s+(?:PtrSafe\s+)?(?:Function|Sub)\b[^\n]+\bLib\b", Opt)),
        ("Writes in memory (VirtualAlloc, RtlMoveMemory, CreateThread)", new(@"VirtualAlloc|RtlMoveMemory|CreateThread|WriteProcessMemory|CreateRemoteThread|EnumSystemLanguageGroups|CallWindowProc", Opt)),
        ("Changes its own code (VBProject)", new(@"\bVBProject\b|\bVBComponents\b|\bCodeModule\b|AccessVBOM", Opt)),
        ("Runs other code (Application.Run, CallByName, ExecuteExcel4Macro)", new(@"Application\.Run\b|\bCallByName\b|ExecuteExcel4Macro|\bEvaluate\s*\(", Opt)),
        ("Hides strings (StrReverse, Base64)", new(@"\bStrReverse\b|Base64|\bbin\.base64\b", Opt)),
        ("Runs shell scripts on macOS (MacScript, AppleScriptTask, popen)", new(@"\bMacScript\b|\bAppleScriptTask\b|libc\.dylib|\bpopen\b|\bsystem\s*\(", Opt)),
        ("Changes the registry", new(@"\bReg(?:Write|Delete)\b|HKEY_|HKCU\\|HKLM\\", Opt)),
        ("Starts with Windows (Run key, scheduled task, Startup)", new(@"CurrentVersion\\Run|schtasks|\\Startup\\|StartupPath", Opt)),
        ("Sends mail", new(@"Outlook\.Application|\.Send\b|CDO\.Message", Opt)),
    ];

    private static readonly Regex ManyChr = new(@"\bChrW?\$?\s*\(", Opt);

    private static readonly (string Label, Regex Pattern)[] XlmSuspicious =
    [
        ("Runs programs (EXEC)", new(@"\bEXEC\s*\(", Opt)),
        ("Calls DLL functions (CALL, REGISTER)", new(@"\b(?:CALL|REGISTER)\s*\(", Opt)),
        ("Downloads (URLDownloadToFile)", new(@"URLDownloadToFile", Opt)),
        ("Writes files (FOPEN, FWRITE)", new(@"\bF(?:OPEN|WRITE|WRITELN)\s*\(", Opt)),
        ("Writes formulas (FORMULA, SET.VALUE)", new(@"\b(?:FORMULA(?:\.FILL|\.ARRAY)?|SET\.VALUE|SET\.NAME)\s*\(", Opt)),
        ("Runs other macros (RUN, GOTO)", new(@"\b(?:RUN|GOTO)\s*\(", Opt)),
        ("Reads the environment (GET.WORKSPACE)", new(@"GET\.WORKSPACE|GET\.WINDOW", Opt)),
        ("Hides strings (CHAR)", new(@"\bCHAR\s*\(", Opt)),
    ];

    private static readonly Regex Url = new(@"\bhttps?://[^\s""'<>()\]\[]+", Opt);
    private static readonly Regex Ip = new(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", Opt);

    public static (List<string> AutoExec, List<string> Suspicious, List<string> Iocs) Vba(string code)
    {
        var auto = VbaAutoExec.Matches(code).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var bad = VbaSuspicious.Where(s => s.Pattern.IsMatch(code)).Select(s => s.Label).ToList();
        if (ManyChr.Matches(code).Count >= 20)
            bad.Add("Builds strings from character codes (many Chr)");
        return (auto, bad, Iocs(code));
    }

    public static (List<string> Suspicious, List<string> Iocs) Xlm(string formulas)
    {
        var bad = XlmSuspicious.Where(s => s.Pattern.IsMatch(formulas)).Select(s => s.Label).ToList();
        return (bad, Iocs(formulas));
    }

    public static List<string> Iocs(string code) =>
        Url.Matches(code).Select(m => m.Value.TrimEnd('.', ',', ';'))
            .Concat(Ip.Matches(code).Select(m => m.Value).Where(ip => ip is not "0.0.0.0" and not "127.0.0.1" && !Url.IsMatch(ip)))
            .Distinct().Take(50).ToList();

    public static Severity Rate(int autoExec, int suspicious) => (autoExec > 0, suspicious > 0) switch
    {
        (true, true) => Severity.High,
        (true, false) or (false, true) => Severity.Medium,
        _ => Severity.Low,
    };

    /// <summary>The lines of code of a VBA module, without the <c>Attribute VB_…</c> lines added by the editor.</summary>
    public static int CodeLines(string code) =>
        code.Split('\n').Count(l => l.Trim() is { Length: > 0 } t && !t.StartsWith("Attribute VB_", StringComparison.OrdinalIgnoreCase));
}
