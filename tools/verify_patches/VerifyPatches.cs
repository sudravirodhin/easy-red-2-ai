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
        return 0;
    }
}
