using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

internal static class VerifyPatches
{
    public static int Main(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("Usage: VerifyPatches <modDll> <interopDir> <coreDir> <dotnetDir>");
            return 2;
        }

        string modDll = args[0];
        string interopDir = args[1];
        string coreDir = args[2];
        string dotnetDir = args[3];

        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string dir in new[] { interopDir, coreDir, dotnetDir })
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p))
                    return Assembly.LoadFrom(p);
            }
            return null;
        };

        Assembly modAsm = Assembly.LoadFrom(modDll);
        int checkedCount = 0;
        int mismatchCount = 0;

        HashSet<Type> registeredInLoad = new HashSet<Type>();
        Type pluginType = modAsm.GetType("EasyRed2HumanAI.Plugin");
        MethodInfo loadMethod = pluginType?.GetMethod("Load", BindingFlags.Public | BindingFlags.Instance);
        byte[] loadIl = loadMethod?.GetMethodBody()?.GetILAsByteArray();
        if (loadIl != null)
        {
            for (int i = 0; i <= loadIl.Length - 5; i++)
            {
                // CIL opcode 0xD0 = ldtoken <int32 metadataToken> (TypeDef=0x02, TypeRef=0x01, TypeSpec=0x1B)
                if (loadIl[i] == 0xD0)
                {
                    int token = BitConverter.ToInt32(loadIl, i + 1);
                    int table = (token >> 24) & 0xFF;
                    if (table == 0x02 || table == 0x01 || table == 0x1B)
                    {
                        try
                        {
                            Type resolved = modAsm.ManifestModule.ResolveType(token);
                            if (resolved != null)
                                registeredInLoad.Add(resolved);
                        }
                        catch { }
                    }
                }
            }
        }

        foreach (Type t in modAsm.GetTypes())
        {
            var attrs = t.GetCustomAttributesData().Where(a => a.AttributeType.Name == "HarmonyPatch").ToList();
            if (attrs.Count == 0)
                continue;

            if (registeredInLoad.Count > 0 && !registeredInLoad.Contains(t))
            {
                Console.Error.WriteLine($"  [HARMONY ERROR] Unregistered patch class (missing SafePatch in Plugin.Load): {t.FullName}");
                mismatchCount++;
            }

            Type targetType = null;
            string methodName = null;
            Type[] argTypes = null;
            int methodType = 0; // HarmonyLib.MethodType: 0=Normal, 1=Getter, 2=Setter

            foreach (CustomAttributeData a in attrs)
            {
                foreach (CustomAttributeTypedArgument arg in a.ConstructorArguments)
                {
                    if (arg.ArgumentType == typeof(Type))
                        targetType = (Type)arg.Value;
                    else if (arg.ArgumentType == typeof(string))
                        methodName = (string)arg.Value;
                    else if (arg.ArgumentType == typeof(Type[]))
                        argTypes = ((IReadOnlyCollection<CustomAttributeTypedArgument>)arg.Value).Select(x => (Type)x.Value).ToArray();
                    else if (arg.ArgumentType.Name == "MethodType")
                        methodType = Convert.ToInt32(arg.Value);
                }
            }

            if (targetType == null || methodName == null)
                continue;

            string lookupMethodName = methodType == 1 ? "get_" + methodName
                                    : methodType == 2 ? "set_" + methodName
                                    : methodName;

            var methods = targetType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == lookupMethodName)
                .ToList();
            if (argTypes != null)
                methods = methods.Where(m => m.GetParameters().Select(p => p.ParameterType).SequenceEqual(argTypes)).ToList();

            if (methods.Count == 0)
            {
                Console.Error.WriteLine($"  [HARMONY ERROR] Missing target: {t.FullName} -> {targetType.FullName}.{lookupMethodName}");
                mismatchCount++;
                continue;
            }

            MethodInfo targetMethod = methods[0];
            if (methodType == 1 || methodType == 2)
            {
                bool hasCallerCount = targetMethod.GetCustomAttributesData().Any(ca => ca.AttributeType.Name == "CallerCountAttribute");
                if (!hasCallerCount)
                {
                    Console.Error.WriteLine($"  [HARMONY ERROR] Non-native IL2CPP field accessor cannot be Harmony-patched: {t.FullName} -> {targetType.FullName}.{lookupMethodName}");
                    mismatchCount++;
                    continue;
                }
            }
            ParameterInfo[] targetParams = targetMethod.GetParameters();
            checkedCount++;

            foreach (MethodInfo patchMethod in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (patchMethod.Name != "Prefix" && patchMethod.Name != "Postfix")
                    continue;

                foreach (ParameterInfo pp in patchMethod.GetParameters())
                {
                    Type pType = pp.ParameterType.IsByRef ? pp.ParameterType.GetElementType() : pp.ParameterType;
                    if (pp.Name == "__instance")
                    {
                        if (targetMethod.IsStatic || !pType.IsAssignableFrom(targetType))
                        {
                            Console.Error.WriteLine($"  [HARMONY ERROR] {t.Name}.{patchMethod.Name} __instance: expected {targetType.Name}, got {pType.Name}");
                            mismatchCount++;
                        }
                    }
                    else if (pp.Name == "__result")
                    {
                        if (!pType.IsAssignableFrom(targetMethod.ReturnType))
                        {
                            Console.Error.WriteLine($"  [HARMONY ERROR] {t.Name}.{patchMethod.Name} __result: expected {targetMethod.ReturnType.Name}, got {pType.Name}");
                            mismatchCount++;
                        }
                    }
                    else if (pp.Name == "__state")
                    {
                        continue;
                    }
                    else if (pp.Name.StartsWith("__", StringComparison.Ordinal) && int.TryParse(pp.Name.Substring(2), out int idx))
                    {
                        Type targetParamType = idx < targetParams.Length
                            ? (targetParams[idx].ParameterType.IsByRef ? targetParams[idx].ParameterType.GetElementType() : targetParams[idx].ParameterType)
                            : null;
                        if (idx >= targetParams.Length || !pType.IsAssignableFrom(targetParamType))
                        {
                            string actualSig = string.Join(", ", targetParams.Select(x => $"{x.ParameterType.Name} {x.Name}"));
                            string exp = idx < targetParams.Length ? targetParamType.Name : "OUT_OF_RANGE";
                            Console.Error.WriteLine($"  [HARMONY ERROR] {t.Name}.{patchMethod.Name} on {targetType.Name}.{methodName}({actualSig}): {pp.Name} is {pType.Name}, expected {exp}");
                            mismatchCount++;
                        }
                    }
                    else if (!pp.Name.StartsWith("__", StringComparison.Ordinal))
                    {
                        ParameterInfo match = targetParams.FirstOrDefault(x => x.Name == pp.Name);
                        Type matchType = match != null && match.ParameterType.IsByRef ? match.ParameterType.GetElementType() : match?.ParameterType;
                        if (match == null || !pType.IsAssignableFrom(matchType))
                        {
                            string actualSig = string.Join(", ", targetParams.Select(x => $"{x.ParameterType.Name} {x.Name}"));
                            Console.Error.WriteLine($"  [HARMONY ERROR] {t.Name}.{patchMethod.Name} on {targetType.Name}.{methodName}({actualSig}): named param '{pp.Name}' ({pType.Name}) does not match");
                            mismatchCount++;
                        }
                    }
                }
            }
        }

        if (mismatchCount > 0)
            return 1;

        Console.WriteLine($"  -> Verified {checkedCount} HarmonyPatch signatures against IL2CPP interop (0 mismatches)");

        string workspaceDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(modDll), ".."));
        if (VerifyAnimatorStrings(workspaceDir) > 0)
            return 1;

        try
        {
            GenerateModIndex(workspaceDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARN] Failed to update mod_index.md: {ex.Message}");
        }

        return 0;
    }

    private struct ModOutlineEntry
    {
        public string Kind;
        public string Tag;
        public string Name;
        public int StartLine;
        public int EndLine;
        public int OpenDepth;
    }

    private static string CollapseSpaces(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length);
        bool prevSpace = true;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        int len = sb.Length;
        if (len > 0 && sb[len - 1] == ' ')
            sb.Length = len - 1;
        return sb.ToString();
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static string ExtractWordAfter(string sig, string keyword)
    {
        int idx = sig.IndexOf(" " + keyword + " ", StringComparison.Ordinal);
        if (idx < 0 && sig.StartsWith(keyword + " ", StringComparison.Ordinal))
            idx = -1;
        else if (idx < 0)
            return null;

        int start = idx + keyword.Length + 2;
        while (start < sig.Length && sig[start] == ' ') start++;
        int end = start;
        while (end < sig.Length && IsIdentChar(sig[end])) end++;
        return end > start ? sig.Substring(start, end - start) : null;
    }

    private static string TryExtractMethodName(string sig)
    {
        int paren = sig.IndexOf('(');
        if (paren <= 0)
            return null;

        string beforeParen = sig.Substring(0, paren).TrimEnd();
        if (beforeParen.IndexOf('=') >= 0)
            return null;

        if (!(beforeParen.StartsWith("public ", StringComparison.Ordinal) ||
              beforeParen.StartsWith("private ", StringComparison.Ordinal) ||
              beforeParen.StartsWith("internal ", StringComparison.Ordinal) ||
              beforeParen.StartsWith("protected ", StringComparison.Ordinal) ||
              beforeParen.StartsWith("static ", StringComparison.Ordinal)))
        {
            return null;
        }

        int i = beforeParen.Length - 1;
        if (i >= 0 && beforeParen[i] == '>')
        {
            int gDepth = 1;
            i--;
            while (i >= 0 && gDepth > 0)
            {
                if (beforeParen[i] == '>') gDepth++;
                else if (beforeParen[i] == '<') gDepth--;
                i--;
            }
            while (i >= 0 && beforeParen[i] == ' ') i--;
        }

        int end = i + 1;
        while (i >= 0 && IsIdentChar(beforeParen[i])) i--;
        int start = i + 1;
        return end > start ? beforeParen.Substring(start, end - start) : null;
    }

    private static string TryFormatHarmonyPatchTag(string trimmedLine)
    {
        int start = trimmedLine.IndexOf('(');
        int end = trimmedLine.LastIndexOf(')');
        if (start < 0 || end <= start)
            return null;

        string raw = trimmedLine.Substring(start + 1, end - start - 1)
            .Replace("typeof(", "")
            .Replace("nameof(", "")
            .Replace(")", "")
            .Replace("\"", "")
            .Replace("MethodType.", "");
        return $"[Patch {CollapseSpaces(raw)}]";
    }

    private static List<ModOutlineEntry> ScanCSharpOutline(string[] lines)
    {
        var completed = new List<ModOutlineEntry>(32);
        var active = new Stack<ModOutlineEntry>(16);

        int depth = 0;
        bool inBlockComment = false;
        var hdr = new System.Text.StringBuilder(256);
        int hdrStartLine = 0;
        string pendingPatchTag = null;

        for (int li = 0; li < lines.Length; li++)
        {
            int lineNum = li + 1;
            string line = lines[li];
            string trimmed = line.Trim();

            if (!inBlockComment && trimmed.StartsWith("[HarmonyPatch", StringComparison.Ordinal))
            {
                string tag = TryFormatHarmonyPatchTag(trimmed);
                if (!string.IsNullOrEmpty(tag))
                    pendingPatchTag = tag;
            }

            if (!inBlockComment && trimmed.StartsWith("[", StringComparison.Ordinal) && hdr.Length == 0)
                continue;

            int n = line.Length;
            bool inStr = false;
            bool inChar = false;
            bool escape = false;
            for (int c = 0; c < n; c++)
            {
                char ch = line[c];
                char next = c + 1 < n ? line[c + 1] : '\0';

                if (inBlockComment)
                {
                    if (ch == '*' && next == '/')
                    {
                        inBlockComment = false;
                        c++;
                    }
                    continue;
                }
                if (inStr)
                {
                    if (escape) escape = false;
                    else if (ch == '\\') escape = true;
                    else if (ch == '"') inStr = false;
                    continue;
                }
                if (inChar)
                {
                    if (escape) escape = false;
                    else if (ch == '\\') escape = true;
                    else if (ch == '\'') inChar = false;
                    continue;
                }

                if (ch == '/' && next == '/') break;
                if (ch == '/' && next == '*') { inBlockComment = true; c++; continue; }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '\'') { inChar = true; continue; }

                if (ch == '{')
                {
                    string sig = CollapseSpaces(hdr.ToString());
                    hdr.Clear();
                    int openLine = hdrStartLine > 0 ? hdrStartLine : lineNum;
                    hdrStartLine = 0;

                    ModOutlineEntry entry = default;
                    bool track = false;

                    if ((depth == 1 || depth == 2) && (sig.Contains(" class ") || sig.Contains(" struct ")))
                    {
                        string tName = ExtractWordAfter(sig, "class") ?? ExtractWordAfter(sig, "struct");
                        if (!string.IsNullOrEmpty(tName))
                        {
                            if (!string.IsNullOrEmpty(pendingPatchTag))
                            {
                                entry = new ModOutlineEntry { Kind = "patch", Tag = pendingPatchTag, Name = tName, StartLine = openLine, OpenDepth = depth };
                                track = true;
                            }
                            else if (depth == 1 && sig.Contains(" struct "))
                            {
                                entry = new ModOutlineEntry { Kind = "type", Name = "struct " + tName, StartLine = openLine, OpenDepth = depth };
                                track = true;
                            }
                        }
                    }
                    else if (depth >= 2 && depth <= 3 && !sig.Contains("=>"))
                    {
                        string mName = TryExtractMethodName(sig);
                        if (!string.IsNullOrEmpty(mName))
                        {
                            string prefix = active.Count > 0 && active.Peek().Kind == "patch"
                                ? active.Peek().Name + "."
                                : "";
                            entry = new ModOutlineEntry { Kind = "method", Name = prefix + mName, StartLine = openLine, OpenDepth = depth };
                            track = true;
                        }
                    }

                    pendingPatchTag = null;
                    if (track)
                        active.Push(entry);
                    depth++;
                }
                else if (ch == '}')
                {
                    hdr.Clear();
                    hdrStartLine = 0;
                    pendingPatchTag = null;
                    if (depth > 0) depth--;

                    while (active.Count > 0 && active.Peek().OpenDepth >= depth)
                    {
                        var done = active.Pop();
                        done.EndLine = lineNum;
                        int span = done.EndLine - done.StartLine + 1;
                        if (done.Kind == "patch" || span >= 4)
                            completed.Add(done);
                    }
                }
                else if (ch == ';')
                {
                    hdr.Clear();
                    hdrStartLine = 0;
                    pendingPatchTag = null;
                }
                else
                {
                    if (!char.IsWhiteSpace(ch) && hdrStartLine == 0)
                        hdrStartLine = lineNum;
                    if (hdrStartLine > 0 && hdr.Length < 400)
                        hdr.Append(ch);
                }
            }

            if (hdr.Length > 0 && hdr.Length < 400)
                hdr.Append(' ');
        }

        completed.Sort((a, b) => a.StartLine.CompareTo(b.StartLine));
        return completed;
    }

    private static void GenerateModIndex(string workspaceDir)
    {
        string srcRoot = Path.Combine(workspaceDir, @"src\EasyRed2AI");
        string cacheDir = Path.Combine(workspaceDir, @"tools\er2_cache");
        if (!Directory.Exists(srcRoot))
            return;

        Directory.CreateDirectory(cacheDir);
        string indexPath = Path.Combine(cacheDir, "mod_index.md");
        string[] files = Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sb = new System.Text.StringBuilder(32768);
        sb.AppendLine("# EasyRed2AI — Mod File, HarmonyPatch & Method Index");
        sb.AppendLine();
        sb.AppendLine($"- **Indexed Files**: `{files.Length}` (auto-updated on `build.ps1` and `--build-cache`)");
        sb.AppendLine("- Check exact `LStart-LEnd` ranges here before calling `read_file`.");
        sb.AppendLine();

        foreach (string filePath in files)
        {
            string relPath = Path.GetRelativePath(workspaceDir, filePath).Replace('\\', '/');
            string[] lines = File.ReadAllLines(filePath);
            var entries = ScanCSharpOutline(lines);

            sb.AppendLine($"## `{relPath}` ({lines.Length} lines)");
            foreach (var e in entries)
            {
                if (e.Kind == "patch")
                    sb.AppendLine($"- `{e.Tag}` `{e.Name}` (L{e.StartLine}-{e.EndLine})");
                else
                    sb.AppendLine($"- `{e.Name}` (L{e.StartLine}-{e.EndLine})");
            }
            sb.AppendLine();
        }

        File.WriteAllText(indexPath, sb.ToString(), System.Text.Encoding.UTF8);
    }

    private static readonly string[] AnimatorCallMarkers =
    {
        ".SetBool(\"",
        ".GetBool(\"",
        ".SetTrigger(\"",
        ".ResetTrigger(\"",
        ".SetFloat(\"",
        ".SetInteger(\"",
        ".ForceAnimatorState(\""
    };

    private static string StripCSharpCommentsPreservingStrings(string line, ref bool inBlockComment)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        bool inStr = false;
        bool inChar = false;
        bool escape = false;
        int n = line.Length;

        for (int i = 0; i < n; i++)
        {
            char c = line[i];
            char next = i + 1 < n ? line[i + 1] : '\0';

            if (inBlockComment)
            {
                if (c == '*' && next == '/')
                {
                    inBlockComment = false;
                    i++;
                }
                continue;
            }

            if (inStr)
            {
                sb.Append(c);
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '"') inStr = false;
                continue;
            }

            if (inChar)
            {
                sb.Append(c);
                if (escape) escape = false;
                else if (c == '\\') escape = true;
                else if (c == '\'') inChar = false;
                continue;
            }

            if (c == '/' && next == '/')
                break;

            if (c == '/' && next == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            if (c == '"') inStr = true;
            else if (c == '\'') inChar = true;
            sb.Append(c);
        }

        return sb.ToString();
    }

    private static int VerifyAnimatorStrings(string workspaceDir)
    {
        string disasmDir = Path.Combine(workspaceDir, @"tools\er2_cache\disasm");
        string soldierAsm = Path.Combine(disasmDir, "Soldier.asm");
        if (!File.Exists(soldierAsm))
        {
            Console.WriteLine("  [WARN] Native Soldier.asm cache not found; skipping Animator string audit.");
            return 0;
        }

        var nativeStrings = new HashSet<string>(StringComparer.Ordinal);
        foreach (string asmFile in new[] { soldierAsm, Path.Combine(disasmDir, "AnimatorHashes.asm") })
        {
            if (!File.Exists(asmFile))
                continue;

            foreach (string line in File.ReadLines(asmFile))
            {
                int pos = 0;
                while (true)
                {
                    int idx = line.IndexOf("; str=\"", pos, StringComparison.Ordinal);
                    if (idx < 0)
                        break;
                    int valStart = idx + 7;
                    int valEnd = line.IndexOf('"', valStart);
                    if (valEnd > valStart)
                    {
                        nativeStrings.Add(line.Substring(valStart, valEnd - valStart));
                        pos = valEnd + 1;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        string srcRoot = Path.Combine(workspaceDir, @"src\EasyRed2AI");
        if (!Directory.Exists(srcRoot))
            return 0;

        string[] files = Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int checkedStrings = 0;
        int errors = 0;
        foreach (string filePath in files)
        {
            string relPath = Path.GetRelativePath(workspaceDir, filePath).Replace('\\', '/');
            string[] lines = File.ReadAllLines(filePath);
            bool inBlockComment = false;

            for (int li = 0; li < lines.Length; li++)
            {
                string code = StripCSharpCommentsPreservingStrings(lines[li], ref inBlockComment);
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                foreach (string marker in AnimatorCallMarkers)
                {
                    int searchFrom = 0;
                    while (true)
                    {
                        int hit = code.IndexOf(marker, searchFrom, StringComparison.Ordinal);
                        if (hit < 0)
                            break;

                        int valStart = hit + marker.Length;
                        int valEnd = code.IndexOf('"', valStart);
                        if (valEnd <= valStart)
                            break;

                        string paramName = code.Substring(valStart, valEnd - valStart);
                        checkedStrings++;
                        if (!nativeStrings.Contains(paramName))
                        {
                            Console.Error.WriteLine($"  [ANIMATOR ERROR] {relPath}:{li + 1} uses Animator string \"{paramName}\" which does not exist in native Soldier.asm!");
                            errors++;
                        }
                        searchFrom = valEnd + 1;
                    }
                }
            }
        }

        if (errors > 0)
            return errors;

        Console.WriteLine($"  -> Verified {checkedStrings} Animator parameter/state string literals against native Soldier.asm (0 mismatches)");
        return 0;
    }
}
