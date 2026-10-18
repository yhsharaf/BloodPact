using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;

namespace RoseNet
{
    // Developer tool (--rosenet-dump-methods): writes the code address (RVA inside GameAssembly.dll) of every
    // method of a few game classes, so their native code can be decompiled. Each interop method has a static
    // NativeMethodInfoPtr_* field pointing at the IL2CPP MethodInfo, whose first field is the code pointer.
    internal static class MethodDump
    {
        static readonly Type[] Types =
        {
            typeof(SavePoint), typeof(SaveLogic), typeof(ResetableEntity), typeof(SavableEntityBase),
            typeof(MobSpawnerSavableStateTranslator), typeof(BaseMobsSpawner), typeof(MobsSpawner),
            typeof(Damageable), typeof(DamageSystem), typeof(BasicBrain), typeof(MobPackComponent),
            typeof(AIStateComponent), typeof(RoomManager.RoomManagerSystem),
        };

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string name);

        public static unsafe void Write()
        {
            try
            {
                long baseAddr = GetModuleHandleW("GameAssembly.dll").ToInt64();
                var sb = new StringBuilder($"# GameAssembly.dll base 0x{baseAddr:X}\n");
                var fields2 = new StringBuilder();
                // Every type of the game's own code, so calls inside a function can be named.
                var all = new System.Collections.Generic.List<Type>(Types);
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var n = asm.GetName().Name;
                    if (n != "Assembly-CSharp" && !n.StartsWith("MotherBase")) continue;
                    try { all.AddRange(asm.GetTypes()); } catch (ReflectionTypeLoadException e) { foreach (var t in e.Types) if (t != null) all.Add(t); }
                }
                foreach (var t in all)
                {
                    if (t.ContainsGenericParameters) continue; // open generic classes have no single code address
                    FieldInfo[] fields;
                    try { fields = t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public); } catch { continue; }
                    foreach (var f in fields)
                    {
                        // Field offsets too, to read decompiled code ("*(obj + 0x58)" -> the field's name).
                        if (f.Name.StartsWith("NativeFieldInfoPtr_") && f.FieldType == typeof(IntPtr))
                        {
                            try
                            {
                                var fi = (IntPtr)f.GetValue(null);
                                if (fi != IntPtr.Zero)
                                    fields2.AppendLine($"{t.FullName}.{f.Name.Substring("NativeFieldInfoPtr_".Length)}\t+0x{Il2CppInterop.Runtime.IL2CPP.il2cpp_field_get_offset(fi):X}");
                            }
                            catch { }
                            continue;
                        }
                        if (!f.Name.StartsWith("NativeMethodInfoPtr_") || f.FieldType != typeof(IntPtr)) continue;
                        IntPtr info;
                        try { info = (IntPtr)f.GetValue(null); } catch { continue; }
                        if (info == IntPtr.Zero) continue;
                        long code = (*(IntPtr*)info).ToInt64();
                        if (code == 0) continue;
                        sb.AppendLine($"{t.FullName}.{f.Name.Substring("NativeMethodInfoPtr_".Length)}\t0x{code - baseAddr:X}");
                    }
                }
                var path = Path.Combine(Paths.BepInExRootPath, "RoseNetMethods.txt");
                File.WriteAllText(path, sb.ToString());
                File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "RoseNetFields.txt"), fields2.ToString());
                Plugin.Logger.LogInfo($"Wrote method addresses to {path}");
            }
            catch (Exception e) { Plugin.Logger.LogError($"Method dump failed: {e}"); }
        }
    }
}
