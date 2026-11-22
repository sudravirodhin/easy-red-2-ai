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
        var coroutineTargets = new SortedSet<string>(StringComparer.Ordinal);
        var verifiedPatches = new List<(string TargetType, string TargetMethod, string PatchClass, string Hooks)>();

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
            int dIdx = (targetType.Name ?? "").IndexOf("_d__", StringComparison.Ordinal);
            if (dIdx >= 0 && dIdx + 4 < targetType.Name.Length && char.IsDigit(targetType.Name[dIdx + 4]))
            {
                string ownerName = targetType.DeclaringType != null ? $"{targetType.DeclaringType.Name}.{targetType.Name}" : targetType.Name;
                coroutineTargets.Add($"{ownerName}.{lookupMethodName}");
            }

            bool hasPrefix = false;
            bool hasPostfix = false;
            foreach (MethodInfo patchMethod in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (patchMethod.Name == "Prefix")
                    hasPrefix = true;
                else if (patchMethod.Name == "Postfix")
                    hasPostfix = true;
                else
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

            string hookKind = hasPrefix && hasPostfix ? "Prefix+Postfix" : hasPrefix ? "Prefix" : hasPostfix ? "Postfix" : "Other";
            verifiedPatches.Add((targetType.FullName ?? targetType.Name, lookupMethodName, t.Name, hookKind));
        }

        if (mismatchCount > 0)
            return 1;

        Console.WriteLine($"  -> Verified {checkedCount} HarmonyPatch signatures against IL2CPP interop (0 mismatches)");
        if (coroutineTargets.Count > 0)
            Console.WriteLine($"  -> Coroutine patch watch-list ({coroutineTargets.Count}): {string.Join(", ", coroutineTargets)}");

        string workspaceDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(modDll), ".."));
        if (VerifyAnimatorStrings(workspaceDir) > 0)
            return 1;
        if (VerifyStaticCodeSafety(workspaceDir) > 0)
            return 1;

        try
        {
            GenerateModIndex(workspaceDir);
            GenerateModStateAndConfigIndex(modAsm, workspaceDir, verifiedPatches);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARN] Failed to update mod indexes: {ex.Message}");
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

                    if ((depth == 1 || depth == 2) && (sig.Contains(" class ") || sig.Contains(" struct ") || sig.Contains(" enum ")))
                    {
                        string tName = ExtractWordAfter(sig, "class") ?? ExtractWordAfter(sig, "struct") ?? ExtractWordAfter(sig, "enum");
                        if (!string.IsNullOrEmpty(tName))
                        {
                            if (!string.IsNullOrEmpty(pendingPatchTag))
                            {
                                entry = new ModOutlineEntry { Kind = "patch", Tag = pendingPatchTag, Name = tName, StartLine = openLine, OpenDepth = depth };
                                track = true;
                            }
                            else if ((depth == 1 && (sig.Contains(" struct ") || sig.Contains(" enum "))) ||
                                     (depth == 2 && (sig.Contains(" struct ") || sig.Contains(" enum ") || sig.Contains(" class "))))
                            {
                                string kw = sig.Contains(" struct ") ? "struct" : sig.Contains(" enum ") ? "enum" : "class";
                                entry = new ModOutlineEntry { Kind = "type", Tag = kw, Name = tName, StartLine = openLine, OpenDepth = depth };
                                track = true;
                            }
                        }
                    }
                    else if (depth >= 2 && depth <= 3 && !sig.Contains("=>"))
                    {
                        string mName = TryExtractMethodName(sig);
                        if (!string.IsNullOrEmpty(mName))
                        {
                            string prefix = active.Count > 0 && (active.Peek().Kind == "patch" || active.Peek().Kind == "type")
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
                        if (done.Kind == "patch" || done.Kind == "type" || span >= 4)
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

        var loadedFiles = new List<(string RelPath, string[] Lines)>(files.Length);
        foreach (string filePath in files)
        {
            loadedFiles.Add((Path.GetRelativePath(workspaceDir, filePath).Replace('\\', '/'), File.ReadAllLines(filePath)));
        }

        var sb = new System.Text.StringBuilder(32768);
        sb.AppendLine("# EasyRed2AI — Mod File, HarmonyPatch & Method Index");
        sb.AppendLine();
        sb.AppendLine($"- **Indexed Files**: `{files.Length}` (auto-updated on `build.ps1`)");
        sb.AppendLine("- Check exact `LStart-LEnd` ranges here before calling `read_file`.");
        sb.AppendLine();

        sb.AppendLine("## File Map");
        foreach (var lf in loadedFiles)
            sb.AppendLine($"- `{lf.RelPath}` ({lf.Lines.Length} lines)");
        sb.AppendLine();

        foreach (var lf in loadedFiles)
        {
            var entries = ScanCSharpOutline(lf.Lines);
            sb.AppendLine($"## `{lf.RelPath}` ({lf.Lines.Length} lines)");
            foreach (var e in entries)
            {
                if (e.Kind == "patch")
                    sb.AppendLine($"- `{e.Tag}` `{e.Name}` (L{e.StartLine}-{e.EndLine})");
                else if (e.Kind == "type")
                    sb.AppendLine($"- `[{e.Tag}]` `{e.Name}` (L{e.StartLine}-{e.EndLine})");
                else
                    sb.AppendLine($"- `{e.Name}` (L{e.StartLine}-{e.EndLine})");
            }
            sb.AppendLine();
        }

        File.WriteAllText(indexPath, sb.ToString(), System.Text.Encoding.UTF8);
    }

    private static string FormatShortTypeName(Type t)
    {
        if (t == null) return "object";
        if (t.IsArray) return FormatShortTypeName(t.GetElementType()) + "[]";
        if (t == typeof(float)) return "float";
        if (t == typeof(int)) return "int";
        if (t == typeof(uint)) return "uint";
        if (t == typeof(long)) return "long";
        if (t == typeof(ulong)) return "ulong";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(string)) return "string";
        if (t == typeof(double)) return "double";
        if (t == typeof(byte)) return "byte";
        if (t == typeof(short)) return "short";
        if (t == typeof(void)) return "void";
        if (t == typeof(object)) return "object";
        if (t.IsGenericType)
        {
            string baseName = t.Name;
            int tick = baseName.IndexOf('`');
            if (tick >= 0) baseName = baseName.Substring(0, tick);
            string args = string.Join(", ", t.GetGenericArguments().Select(FormatShortTypeName));
            return $"{baseName}<{args}>";
        }
        return t.Name;
    }

    private static bool IsIndexedRuntimeStateType(Type t)
    {
        string ns = t.Namespace ?? "";
        string fullName = t.FullName ?? "";
        if (!ns.StartsWith("EasyRed2", StringComparison.Ordinal) && !fullName.StartsWith("EasyRed2", StringComparison.Ordinal))
            return false;
        if (t.Name.Contains("<") || t.IsEnum)
            return false;

        string name = t.Name;
        return name.EndsWith("Handles", StringComparison.Ordinal) ||
               name.EndsWith("State", StringComparison.Ordinal) ||
               name.EndsWith("Blackboard", StringComparison.Ordinal) ||
               name.EndsWith("Traits", StringComparison.Ordinal) ||
               name.EndsWith("Snapshot", StringComparison.Ordinal) ||
               name.EndsWith("Profile", StringComparison.Ordinal) ||
               name.EndsWith("Info", StringComparison.Ordinal) ||
               name == "ThreatContact" ||
               name == "GoapPlan" ||
               name == "BotMoveOverrideState" ||
               name == "FactionCommandProfile" ||
               name == "VeteranPersonality";
    }

    private static void GenerateModStateAndConfigIndex(
        Assembly modAsm,
        string workspaceDir,
        List<(string TargetType, string TargetMethod, string PatchClass, string Hooks)> verifiedPatches)
    {
        string cacheDir = Path.Combine(workspaceDir, @"tools\er2_cache");
        Directory.CreateDirectory(cacheDir);
        string outPath = Path.Combine(cacheDir, "mod_state_and_config.md");

        var sb = new System.Text.StringBuilder(32768);
        sb.AppendLine("# EasyRed2AI — Reverse HarmonyPatch Map, State Structs & VisionConfig");
        sb.AppendLine();
        sb.AppendLine("- Auto-generated from `EasyRed2HumanAI.dll` reflection on every `build.ps1`.");
        sb.AppendLine();

        sb.AppendLine("## 1. Reverse HarmonyPatch Map (By Vanilla Target Type)");
        sb.AppendLine();
        foreach (var grp in verifiedPatches
            .GroupBy(x => x.TargetType, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"### `{grp.Key}`");
            foreach (var item in grp.OrderBy(x => x.TargetMethod, StringComparer.Ordinal).ThenBy(x => x.PatchClass, StringComparer.Ordinal))
                sb.AppendLine($"- `{item.TargetMethod}` -> `{item.PatchClass}` [`{item.Hooks}`]");
            sb.AppendLine();
        }

        sb.AppendLine("## 2. Core Runtime State Structs & Handle Classes");
        sb.AppendLine();
        foreach (Type t in modAsm.GetTypes().Where(IsIndexedRuntimeStateType).OrderBy(x => x.FullName, StringComparer.Ordinal))
        {
            FieldInfo[] fields;
            try
            {
                fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(f => !f.Name.Contains("<"))
                    .OrderBy(f => f.MetadataToken)
                    .ToArray();
            }
            catch
            {
                continue;
            }
            if (fields.Length == 0)
                continue;

            string display = (t.FullName ?? t.Name);
            if (display.StartsWith("EasyRed2HumanAI.", StringComparison.Ordinal))
                display = display.Substring("EasyRed2HumanAI.".Length);
            display = display.Replace('+', '.');
            string kind = t.IsValueType ? "struct" : "class";
            sb.AppendLine($"### `{display}` ({kind})");
            foreach (FieldInfo f in fields)
                sb.AppendLine($"- `{FormatShortTypeName(f.FieldType)}` `{f.Name}`");
            sb.AppendLine();
        }

        Type vcType = modAsm.GetType("EasyRed2HumanAI.VisionConfig");
        if (vcType != null)
        {
            sb.AppendLine("## 3. VisionConfig Entries");
            sb.AppendLine();
            try
            {
                foreach (FieldInfo f in vcType.GetFields(BindingFlags.Public | BindingFlags.Static).OrderBy(x => x.MetadataToken))
                {
                    if (!f.FieldType.Name.StartsWith("ConfigEntry", StringComparison.Ordinal))
                        continue;
                    Type[] genArgs = f.FieldType.IsGenericType ? f.FieldType.GetGenericArguments() : Array.Empty<Type>();
                    string innerType = genArgs.Length > 0 ? FormatShortTypeName(genArgs[0]) : FormatShortTypeName(f.FieldType);
                    sb.AppendLine($"- `{f.Name}` (`{innerType}`)");
                }
            }
            catch { }
            sb.AppendLine();
        }

        File.WriteAllText(outPath, sb.ToString(), System.Text.Encoding.UTF8);
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

    private static bool ContainsWholeWordBefore(string s, int limit, string word)
    {
        int start = 0;
        while (start < limit)
        {
            int idx = s.IndexOf(word, start, limit - start, StringComparison.Ordinal);
            if (idx < 0)
                return false;
            bool leftOk = idx == 0 || !IsIdentChar(s[idx - 1]);
            int after = idx + word.Length;
            bool rightOk = after >= s.Length || !IsIdentChar(s[after]);
            if (leftOk && rightOk)
                return true;
            start = idx + 1;
        }
        return false;
    }

    private static bool ContainsIdentPrefix(string haystack, string needle)
    {
        int start = 0;
        while (start < haystack.Length)
        {
            int idx = haystack.IndexOf(needle, start, StringComparison.Ordinal);
            if (idx < 0)
                return false;
            if (idx == 0 || !IsIdentChar(haystack[idx - 1]))
                return true;
            start = idx + 1;
        }
        return false;
    }

    private static bool ContainsPruneDictCall(string haystack, string dictName)
    {
        int start = 0;
        while (start < haystack.Length)
        {
            int idx = haystack.IndexOf("PruneDict(", start, StringComparison.Ordinal);
            if (idx < 0)
                return false;
            int p = idx + "PruneDict(".Length;
            while (p < haystack.Length && char.IsWhiteSpace(haystack[p]))
                p++;
            if (p + dictName.Length <= haystack.Length &&
                string.CompareOrdinal(haystack, p, dictName, 0, dictName.Length) == 0 &&
                (p + dictName.Length >= haystack.Length || !IsIdentChar(haystack[p + dictName.Length])))
            {
                return true;
            }
            start = idx + 1;
        }
        return false;
    }

    private static int VerifyStaticCodeSafety(string workspaceDir)
    {
        string srcRoot = Path.Combine(workspaceDir, @"src\EasyRed2AI");
        if (!Directory.Exists(srcRoot))
            return 0;

        string[] files = Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        int errors = 0;
        var intPtrDicts = new List<(string RelPath, int LineNo, string Name)>();
        var strippedSources = new List<string>(files.Length);

        string[] forbiddenIl2CppMarkers =
        {
            "TryCast<TerrainCollider>(",
            "GetComponent<TerrainCollider>(",
            "GetComponent<ImpactSpecifier>(",
            "GetComponent<Spottable>(",
            ".ForceControllerEnabled("
        };

        foreach (string filePath in files)
        {
            string relPath = Path.GetRelativePath(workspaceDir, filePath).Replace('\\', '/');
            string rawText = File.ReadAllText(filePath);
            string[] lines = File.ReadAllLines(filePath);
            bool inBlockComment = false;
            var fileStripped = new System.Text.StringBuilder(rawText.Length);
            bool hasDllImport = false;

            for (int li = 0; li < lines.Length; li++)
            {
                string code = StripCSharpCommentsPreservingStrings(lines[li], ref inBlockComment);
                fileStripped.AppendLine(code);
                if (string.IsNullOrWhiteSpace(code))
                    continue;

                foreach (string bad in forbiddenIl2CppMarkers)
                {
                    if (code.Contains(bad, StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"  [STATIC LINT ERROR] {relPath}:{li + 1} uses unsafe IL2CPP pattern '{bad}'");
                        errors++;
                    }
                }

                if (code.Contains("[DllImport(", StringComparison.Ordinal))
                    hasDllImport = true;

                int dictIdx = code.IndexOf("Dictionary<IntPtr,", StringComparison.Ordinal);
                int eqIdx = dictIdx >= 0 ? code.IndexOf('=') : -1;
                if (dictIdx >= 0 && (eqIdx < 0 || dictIdx < eqIdx) && ContainsWholeWordBefore(code, dictIdx, "static"))
                {
                    int depth = 1;
                    int pos = dictIdx + "Dictionary<".Length;
                    while (pos < code.Length && depth > 0)
                    {
                        if (code[pos] == '<') depth++;
                        else if (code[pos] == '>') depth--;
                        pos++;
                    }
                    while (pos < code.Length && char.IsWhiteSpace(code[pos]))
                        pos++;
                    int idStart = pos;
                    while (pos < code.Length && IsIdentChar(code[pos]))
                        pos++;
                    if (pos > idStart && !char.IsDigit(code[idStart]))
                        intPtrDicts.Add((relPath, li + 1, code.Substring(idStart, pos - idStart)));
                }
            }

            string noComments = fileStripped.ToString();
            strippedSources.Add(noComments);

            if (hasDllImport && !noComments.Contains("OperatingSystem.IsWindows()", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"  [STATIC LINT ERROR] {relPath} uses [DllImport] without an OperatingSystem.IsWindows() runtime guard.");
                errors++;
            }
        }

        string allCodeNoComments = string.Join("\n", strippedSources);
        foreach (var d in intPtrDicts)
        {
            bool hasClean =
                ContainsIdentPrefix(allCodeNoComments, d.Name + ".Clear(") ||
                ContainsIdentPrefix(allCodeNoComments, d.Name + ".Remove(") ||
                ContainsPruneDictCall(allCodeNoComments, d.Name);
            if (!hasClean)
            {
                Console.Error.WriteLine($"  [STATIC LINT ERROR] {d.RelPath}:{d.LineNo} static Dictionary<IntPtr, ...> '{d.Name}' has no Clear/Remove/PruneDict eviction path.");
                errors++;
            }
        }

        if (errors > 0)
            return errors;

        Console.WriteLine($"  -> Verified {intPtrDicts.Count} static IntPtr dictionaries have Clear/Prune/Remove cleanup (0 leaks) & 0 IL2CPP footguns");
        return 0;
    }
}
