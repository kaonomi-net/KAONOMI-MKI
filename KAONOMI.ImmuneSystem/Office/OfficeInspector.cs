namespace KAONOMI.ImmuneSystem.Office;

using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using KAONOMI.ImmuneSystem.Scanning;

/// <summary>
/// Looks for code, and for the ways to run code, in an Office file: Word, Excel, PowerPoint, Visio (2007+ packages and
/// 97-2003 compound files) and RTF. The embedded documents are opened too (three levels deep).
/// </summary>
public sealed class OfficeInspector(Options options)
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx", ".dotm", ".doc", ".dot", ".rtf",
        ".xlsx", ".xlsm", ".xltx", ".xltm", ".xlsb", ".xlam", ".xls", ".xlt", ".xla",
        ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm", ".ppam", ".sldm", ".ppt", ".pot", ".pps",
        ".vsdx", ".vsdm", ".vstm",
    };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".scr", ".com", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf",
        ".wsh", ".hta", ".jar", ".py", ".sh", ".command", ".app", ".msi", ".lnk", ".reg", ".cpl", ".applescript", ".scpt",
    };

    private const int MaxDepth = 3;
    private const long MaxEntry = 100 << 20;

    private static readonly Regex DdeField = new(@"^\s*DDE(?:AUTO)?\b", RegexOptions.IgnoreCase);
    private static readonly Regex DdeFormula = new(@"[A-Za-z0-9_.]+\|'[^']*'![A-Za-z0-9_$]+", RegexOptions.CultureInvariant);
    private static readonly Regex Formula = new(@"<(?:\w+:)?f\b[^>]*>([^<]*)</(?:\w+:)?f>", RegexOptions.CultureInvariant);

    public OfficeFile? Inspect(FileInfo file)
    {
        // Many files share these extensions (the .doc of Lucene, Word 2003 XML…): the first bytes say whether it is Office.
        using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Span<byte> head = stackalloc byte[8];
            if (fs.ReadAtLeast(head, 8, throwOnEndOfStream: false) < 8
                || !(head[..4].SequenceEqual("PK\x03\x04"u8) || CompoundFile.IsCompoundFile(head, headerOnly: true) || head[..5].SequenceEqual("{\\rtf"u8)))
                return null;
        }
        var data = File.ReadAllBytes(file.FullName);
        var findings = new List<CodeFinding>();
        var container = Inspect(data, file.Name, "", 0, findings);
        if (container is null || findings.Count == 0)
            return null;
        return new OfficeFile
        {
            Path = file.FullName,
            Size = file.Length,
            Modified = file.LastWriteTime,
            Format = file.Extension.TrimStart('.').ToLowerInvariant(),
            Container = container,
            Severity = findings.Max(f => f.Severity),
            Findings = findings,
        };
    }

    /// <summary>Adds the findings of a file (or of an embedded file); its container, or null when it is not an Office file.</summary>
    private string? Inspect(byte[] data, string name, string prefix, int depth, List<CodeFinding> findings)
    {
        if (data.Length >= 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4)
        {
            Package(data, prefix, depth, findings);
            return "ooxml";
        }
        if (CompoundFile.IsCompoundFile(data))
        {
            Compound(data, name, prefix, depth, findings);
            return "cfb";
        }
        if (data.Length >= 5 && data.AsSpan(0, 5).SequenceEqual("{\\rtf"u8))
        {
            Rtf(data, prefix, findings);
            return "rtf";
        }
        return null;
    }

    // ---- Office 2007+ packages ------------------------------------------------------------------------------------

    private void Package(byte[] data, string prefix, int depth, List<CodeFinding> findings)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var codeText = new List<(string Location, List<CodeText.Line> Lines)>();
        var activeX = new List<string>();
        string? xlmAuto = null;
        var xlmIndex = findings.Count;

        foreach (var entry in zip.Entries)
        {
            if (entry.Length > MaxEntry || entry.FullName.EndsWith('/'))
                continue;
            var path = entry.FullName;
            var lower = path.ToLowerInvariant();
            var location = prefix + path;

            if (lower.EndsWith("vbaproject.bin"))
            {
                Vba(new CompoundFile(Bytes(entry)), location + "/", findings);
            }
            else if (lower.Contains("/macrosheets/"))
            {
                if (lower.EndsWith(".xml"))
                    XlmSheet(Text(entry), location, findings);
                else if (lower.EndsWith(".bin"))
                    findings.Add(new CodeFinding
                    {
                        Kind = "xlm-macro",
                        Severity = Severity.Medium,
                        Location = location,
                        Description = "Excel 4.0 (XLM) macro sheet, in the binary format of .xlsb (formulas not decoded)",
                    });
            }
            else if (lower.Contains("/activex/") && lower.EndsWith(".xml"))
            {
                var classId = Regex.Match(Text(entry), @"classid=""([^""]+)""", RegexOptions.IgnoreCase);
                activeX.Add(classId.Success ? classId.Groups[1].Value : Path.GetFileName(path));
            }
            else if (lower.Contains("/embeddings/"))
            {
                Embedded(Bytes(entry), path, location, depth, findings);
            }
            else if (lower.EndsWith(".rels"))
            {
                Relationships(Text(entry), location, findings);
            }
            else if (lower.StartsWith("customui/") && lower.EndsWith(".xml"))
            {
                RibbonCallbacks(Text(entry), location, findings);
            }
            else if (lower.Contains("webextensions/webextension") && lower.EndsWith(".xml"))
            {
                AddIn(Text(entry), location, findings);
            }
            else if (lower.StartsWith("customxml/item") && lower.EndsWith(".xml"))
            {
                var xml = Text(entry);
                if (xml.Contains("DataMashup", StringComparison.Ordinal))
                    PowerQuery(xml, location, findings);
            }
            else if (lower.StartsWith("xl/worksheets/") && lower.EndsWith(".xml"))
            {
                Worksheet(Text(entry), location, findings);
            }
            else if (lower.StartsWith("xl/externallinks/") && lower.EndsWith(".xml"))
            {
                var xml = Text(entry);
                var dde = Regex.Match(xml, @"<ddeLink[^>]*ddeService=""([^""]*)""[^>]*ddeTopic=""([^""]*)""");
                if (dde.Success)
                    findings.Add(new CodeFinding
                    {
                        Kind = "dde",
                        Severity = Severity.High,
                        Location = location,
                        Description = $"DDE link: runs {WebUtility.HtmlDecode(dde.Groups[1].Value)} with {WebUtility.HtmlDecode(dde.Groups[2].Value)}",
                        Code = options.NoCode ? null : WebUtility.HtmlDecode(dde.Value),
                    });
            }
            else if (lower == "xl/workbook.xml")
            {
                var auto = Regex.Match(Text(entry), @"<definedName[^>]*name=""(_xlnm\.)?(Auto_(?:Open|Close|Activate)\w*)""[^>]*>([^<]*)<", RegexOptions.IgnoreCase);
                if (auto.Success)
                    xlmAuto = $"{auto.Groups[2].Value} → {WebUtility.HtmlDecode(auto.Groups[3].Value)}";
            }
            else if (Regex.IsMatch(lower, @"^word/(document|header\d*|footer\d*|footnotes|endnotes)\.xml$"))
            {
                var xml = Text(entry);
                WordFields(xml, location, findings);
                if (options.CodeText)
                    codeText.Add((location, CodeText.Word(new MemoryStream(Encoding.UTF8.GetBytes(xml)))));
            }
            else if (options.CodeText && Regex.IsMatch(lower, @"^ppt/(slides/slide|notesslides/notesslide)\d+\.xml$"))
            {
                using var s = entry.Open();
                codeText.Add((location, CodeText.Slide(s)));
            }
            else if (CodeExtensions.Contains(Path.GetExtension(lower)))
            {
                findings.Add(EmbeddedFile(path, location, entry.Length));
            }
        }

        if (activeX.Count > 0)
            findings.Add(new CodeFinding
            {
                Kind = "activex",
                Severity = Severity.Medium,
                Location = prefix + "activeX/",
                Description = $"{activeX.Count} ActiveX control(s): their events can run VBA code ({string.Join(", ", activeX.Distinct().Take(10))})",
            });

        // An Auto_Open name makes the macro sheets run when the workbook opens.
        if (xlmAuto is not null)
            for (var i = xlmIndex; i < findings.Count; i++)
                if (findings[i].Kind == "xlm-macro")
                    findings[i] = findings[i] with { AutoExec = [xlmAuto], Severity = Severity.High };

        foreach (var group in codeText.GroupBy(c => c.Location.Contains("/slides/") || c.Location.Contains("/notesSlides/", StringComparison.OrdinalIgnoreCase) ? prefix + "ppt/slides" : c.Location))
        {
            var lines = group.SelectMany(g => g.Lines.Append(new CodeText.Line("", false, false))).ToList();
            if (CodeText.Find(lines, group.Key, options.MaxCode, options.NoCode) is { } found)
                findings.Add(found);
        }
    }

    private void XlmSheet(string xml, string location, List<CodeFinding> findings)
    {
        var formulas = Formula.Matches(xml).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).Where(f => f.Length > 0).ToList();
        var code = string.Join("\n", formulas);
        var (bad, iocs) = CodeAnalysis.Xlm(code);
        findings.Add(new CodeFinding
        {
            Kind = "xlm-macro",
            Severity = bad.Count > 0 ? Severity.High : Severity.Medium,
            Location = location,
            Description = $"Excel 4.0 (XLM) macro sheet, {formulas.Count} formula(s)",
            Suspicious = bad.Count > 0 ? bad : null,
            Iocs = iocs.Count > 0 ? iocs : null,
            Lines = formulas.Count,
            Code = options.NoCode ? null : CodeText.Cut(code, options.MaxCode),
        });
    }

    private void Worksheet(string xml, string location, List<CodeFinding> findings)
    {
        if (!xml.Contains("<f", StringComparison.Ordinal))
            return;
        var formulas = Formula.Matches(xml).Select(m => WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        var python = formulas.Where(f => f.Contains("_xlws.PY(", StringComparison.OrdinalIgnoreCase)).ToList();
        if (python.Count > 0)
            findings.Add(new CodeFinding
            {
                Kind = "python-in-excel",
                Severity = Severity.Low,
                Location = location,
                Description = $"{python.Count} Python in Excel cell(s) (=PY), run in the Microsoft cloud",
                Lines = python.Count,
                Code = options.NoCode ? null : CodeText.Cut(string.Join("\n", python), options.MaxCode),
            });
        var dde = formulas.Where(f => DdeFormula.IsMatch(f)).ToList();
        if (dde.Count > 0)
            findings.Add(new CodeFinding
            {
                Kind = "dde",
                Severity = Severity.High,
                Location = location,
                Description = $"{dde.Count} DDE formula(s) (application|'command'!cell): they can start a program",
                Code = options.NoCode ? null : CodeText.Cut(string.Join("\n", dde), options.MaxCode),
            });
    }

    private void WordFields(string xml, string location, List<CodeFinding> findings)
    {
        var instructions = Regex.Matches(xml, @"<w:instrText[^>]*>([^<]*)</w:instrText>|<w:fldChar[^>]*w:fldCharType=""(begin|end)""|w:instr=""([^""]*)""")
            .Aggregate((Fields: new List<string>(), Current: new StringBuilder()), (acc, m) =>
            {
                if (m.Groups[1].Success)
                    acc.Current.Append(m.Groups[1].Value);
                else if (m.Groups[3].Success)
                    acc.Fields.Add(m.Groups[3].Value);
                else if (acc.Current.Length > 0)
                {
                    acc.Fields.Add(acc.Current.ToString());
                    acc.Current.Clear();
                }
                return acc;
            }).Fields.Select(WebUtility.HtmlDecode).ToList();
        var dde = instructions.Where(f => DdeField.IsMatch(f!)).ToList();
        if (dde.Count > 0)
            findings.Add(new CodeFinding
            {
                Kind = "dde",
                Severity = Severity.High,
                Location = location,
                Description = $"{dde.Count} DDE field(s): they can start a program when the document opens",
                Code = options.NoCode ? null : CodeText.Cut(string.Join("\n", dde), options.MaxCode),
            });
    }

    private static void Relationships(string xml, string location, List<CodeFinding> findings)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return;
        }
        foreach (var rel in doc.Descendants().Where(e => e.Name.LocalName == "Relationship"))
        {
            if (!string.Equals((string?)rel.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
                continue;
            var type = ((string?)rel.Attribute("Type") ?? "").Split('/').Last();
            var target = (string?)rel.Attribute("Target") ?? "";
            var (kind, severity, what) = type switch
            {
                "attachedTemplate" => ("remote-template", Severity.High, "Template loaded from outside the file when it opens (it can bring macros)"),
                "oleObject" => ("external-object", Severity.High, "OLE object loaded from outside the file"),
                "frame" => ("external-object", Severity.Medium, "Frame loaded from outside the file"),
                "subDocument" => ("external-object", Severity.Medium, "Sub-document loaded from outside the file"),
                _ => (null, Severity.Info, null),
            };
            if (kind is null)
                continue;
            if (kind == "remote-template" && !Regex.IsMatch(target, @"^(?:https?:|\\\\|//|file://)", RegexOptions.IgnoreCase))
                severity = Severity.Low; // A template on this machine.
            findings.Add(new CodeFinding
            {
                Kind = kind,
                Severity = severity,
                Location = location,
                Description = $"{what}: {target}",
                Iocs = CodeAnalysis.Iocs(target) is { Count: > 0 } iocs ? iocs : null,
            });
        }
    }

    private static void RibbonCallbacks(string xml, string location, List<CodeFinding> findings)
    {
        var callbacks = Regex.Matches(xml, @"\b(on\w+|get\w+|loadImage)=""([^""]+)""")
            .Select(m => $"{m.Groups[1].Value}={m.Groups[2].Value}").Distinct().ToList();
        if (callbacks.Count == 0)
            return;
        var onLoad = callbacks.Where(c => c.StartsWith("onLoad=", StringComparison.Ordinal)).ToList();
        findings.Add(new CodeFinding
        {
            Kind = "ribbon-callbacks",
            Severity = onLoad.Count > 0 ? Severity.Medium : Severity.Low,
            Location = location,
            Description = $"Custom ribbon calling {callbacks.Count} macro(s)",
            AutoExec = onLoad.Count > 0 ? onLoad : null,
            Code = string.Join("\n", callbacks.Take(100)),
        });
    }

    private static void AddIn(string xml, string location, List<CodeFinding> findings)
    {
        var reference = Regex.Match(xml, @"<we:reference[^>]*/?>");
        var id = Regex.Match(reference.Value, @"\bid=""([^""]*)""");
        var store = Regex.Match(reference.Value, @"\bstore=""([^""]*)""");
        findings.Add(new CodeFinding
        {
            Kind = "office-addin",
            Severity = Severity.Info,
            Location = location,
            Description = $"Office add-in (JavaScript, loaded from {(store.Success && store.Groups[1].Value.Length > 0 ? store.Groups[1].Value : "its store")}): {(id.Success ? id.Groups[1].Value : "unknown id")}",
        });
    }

    /// <summary>Power Query: the <c>DataMashup</c> holds, in base64, a zip whose <c>Formulas/Section1.m</c> is the M code.</summary>
    private void PowerQuery(string xml, string location, List<CodeFinding> findings)
    {
        var m = Regex.Match(xml, @"<DataMashup[^>]*>\s*([A-Za-z0-9+/=\s]+?)\s*</DataMashup>");
        string? code = null;
        if (m.Success)
        {
            try
            {
                var bytes = Convert.FromBase64String(m.Groups[1].Value);
                var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
                using var zip = new ZipArchive(new MemoryStream(bytes, 8, length), ZipArchiveMode.Read);
                var section = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".m", StringComparison.OrdinalIgnoreCase));
                if (section is not null)
                    code = Text(section);
            }
            catch (Exception ex) when (ex is FormatException or InvalidDataException or ArgumentException)
            {
                code = null;
            }
        }
        var web = code is not null && Regex.IsMatch(code, @"\b(?:Web\.Contents|Web\.Page|OData\.Feed|Sql\.Database|Odbc\.|OleDb\.|Python\.Execute|R\.Execute)\b");
        findings.Add(new CodeFinding
        {
            Kind = "power-query",
            Severity = web ? Severity.Medium : Severity.Low,
            Location = location,
            Description = web ? "Power Query (M) queries that read the web, a database or run scripts" : "Power Query (M) queries",
            Iocs = code is not null && CodeAnalysis.Iocs(code) is { Count: > 0 } iocs ? iocs : null,
            Lines = code?.Split('\n').Length,
            Code = options.NoCode || code is null ? null : CodeText.Cut(code, options.MaxCode),
        });
    }

    private void Embedded(byte[] data, string path, string location, int depth, List<CodeFinding> findings)
    {
        if (CodeExtensions.Contains(Path.GetExtension(path)))
        {
            findings.Add(EmbeddedFile(path, location, data.Length));
            return;
        }
        if (depth >= MaxDepth)
            return;
        Inspect(data, Path.GetFileName(path), location + "!/", depth + 1, findings);
    }

    private static CodeFinding EmbeddedFile(string name, string location, long size) => new()
    {
        Kind = "embedded-file",
        Severity = Severity.High,
        Location = location,
        Description = $"Embedded program or script: {Path.GetFileName(name)} ({size:N0} bytes)",
    };

    // ---- Office 97-2003 compound files ------------------------------------------------------------------------

    private void Compound(byte[] data, string name, string prefix, int depth, List<CodeFinding> findings)
    {
        var cf = new CompoundFile(data);
        var root = cf.Root;
        if (CompoundFile.Child(root, "EncryptedPackage") is not null)
        {
            findings.Add(new CodeFinding
            {
                Kind = "encrypted",
                Severity = Severity.Info,
                Location = prefix + "EncryptedPackage",
                Description = "Protected by a password: its content cannot be inspected",
            });
            return;
        }

        Vba(cf, prefix, findings);

        // Excel 97-2003: the macro sheets are listed in the BOUNDSHEET records of the Workbook stream.
        if ((CompoundFile.Child(root, "Workbook") ?? CompoundFile.Child(root, "Book")) is { IsStream: true } workbook)
            Biff(cf.Read(workbook), prefix + workbook.Name, findings);

        foreach (var e in cf.All().Where(e => e.IsStream))
        {
            if (e.Name == "\u0001Ole10Native")
            {
                var label = Ole10NativeName(cf.Read(e));
                if (label is not null && CodeExtensions.Contains(Path.GetExtension(label)))
                    findings.Add(EmbeddedFile(label, prefix + e.Path, e.Size));
            }
            else if (e.Name == "Equation Native")
            {
                findings.Add(new CodeFinding
                {
                    Kind = "ole-object",
                    Severity = Severity.High,
                    Location = prefix + e.Path,
                    Description = "Equation Editor 3.0 object: the target of the CVE-2017-11882 exploits",
                });
            }
            else if (depth < MaxDepth && e.Name is "Package" or "CONTENTS" && e.Size > 4)
            {
                // An embedded Office 2007+ document.
                var bytes = cf.Read(e);
                if (bytes[0] == 'P' && bytes[1] == 'K')
                    Inspect(bytes, e.Name, prefix + e.Path + "!/", depth + 1, findings);
            }
        }
    }

    private void Vba(CompoundFile cf, string prefix, List<CodeFinding> findings)
    {
        foreach (var vba in VbaProject.Find(cf))
        {
            List<VbaModule> modules;
            try
            {
                modules = VbaProject.Modules(cf, vba);
            }
            catch (InvalidDataException ex)
            {
                findings.Add(new CodeFinding
                {
                    Kind = "vba-macro",
                    Severity = Severity.Medium,
                    Location = prefix + vba.Path,
                    Description = $"VBA project whose source could not be read ({ex.Message})",
                });
                continue;
            }
            foreach (var module in modules)
            {
                var lines = CodeAnalysis.CodeLines(module.Code);
                if (lines == 0)
                    continue;
                var (auto, bad, iocs) = CodeAnalysis.Vba(module.Code);
                findings.Add(new CodeFinding
                {
                    Kind = "vba-macro",
                    Severity = CodeAnalysis.Rate(auto.Count, bad.Count),
                    Location = $"{prefix}{vba.Path}/{module.Name}",
                    Description = $"VBA {module.Kind} {module.Name}, {lines} line(s)" + (auto.Count > 0 ? ", runs by itself" : ""),
                    AutoExec = auto.Count > 0 ? auto : null,
                    Suspicious = bad.Count > 0 ? bad : null,
                    Iocs = iocs.Count > 0 ? iocs : null,
                    Lines = lines,
                    Code = options.NoCode ? null : CodeText.Cut(module.Code, options.MaxCode),
                });
            }
        }
    }

    private static void Biff(byte[] stream, string location, List<CodeFinding> findings)
    {
        var pos = 0;
        while (pos + 4 <= stream.Length)
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(pos));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(pos + 2));
            pos += 4;
            if (pos + length > stream.Length)
                break;
            if (id == 0x0085 && length >= 8)
            {
                var state = stream[pos + 4] & 0x03;
                var type = stream[pos + 5];
                if (type == 0x01)
                {
                    var count = stream[pos + 6];
                    var wide = (stream[pos + 7] & 1) != 0;
                    var nameLength = Math.Min(count * (wide ? 2 : 1), length - 8);
                    var sheet = wide ? Encoding.Unicode.GetString(stream, pos + 8, nameLength) : Encoding.Latin1.GetString(stream, pos + 8, nameLength);
                    findings.Add(new CodeFinding
                    {
                        Kind = "xlm-macro",
                        Severity = state == 2 ? Severity.High : Severity.Medium,
                        Location = location,
                        Description = $"Excel 4.0 (XLM) macro sheet \"{sheet}\"" + state switch { 1 => ", hidden", 2 => ", very hidden", _ => "" },
                    });
                }
            }
            else if (id == 0x000A)
            {
                break; // EOF of the workbook globals: the sheets follow.
            }
            pos += length;
        }
    }

    /// <summary>The name of the file wrapped in a Package object (<c>\u0001Ole10Native</c>: size, flags, label, path…).</summary>
    private static string? Ole10NativeName(byte[] data)
    {
        if (data.Length < 8)
            return null;
        var end = Array.IndexOf(data, (byte)0, 6);
        if (end <= 6)
            return null;
        var label = Encoding.Latin1.GetString(data, 6, end - 6);
        return label.Length is > 0 and < 260 ? label : null;
    }

    // ---- RTF -------------------------------------------------------------------------------------------------------

    private static void Rtf(byte[] data, string prefix, List<CodeFinding> findings)
    {
        var text = Encoding.Latin1.GetString(data);
        if (!text.Contains("\\object", StringComparison.Ordinal) && !text.Contains("\\objdata", StringComparison.Ordinal))
            return;
        var classes = Regex.Matches(text, @"\\objclass\s+([^}\\]+)").Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();
        var equation = classes.Any(c => c.StartsWith("Equation", StringComparison.OrdinalIgnoreCase)) || text.Contains("4571756174696f6e", StringComparison.OrdinalIgnoreCase);
        var autoUpdate = text.Contains("\\objupdate", StringComparison.Ordinal);
        findings.Add(new CodeFinding
        {
            Kind = "ole-object",
            Severity = equation || autoUpdate ? Severity.High : Severity.Medium,
            Location = prefix + "\\objdata",
            Description = "RTF with embedded OLE object(s)" + (classes.Count > 0 ? $": {string.Join(", ", classes)}" : "") +
                (equation ? " (Equation Editor: the target of the CVE-2017-11882 exploits)" : "") +
                (autoUpdate ? ", updated when the document opens (\\objupdate)" : ""),
            AutoExec = autoUpdate ? ["\\objupdate"] : null,
        });
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private static byte[] Bytes(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string Text(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var reader = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
