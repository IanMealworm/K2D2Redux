using System;
using System.Reflection;

// Pre-flight loader check used by Tools/build.sh.
//
// Loading a mod assembly is not the same as compiling it. The Unity/Mono loader resolves every type
// referenced by a loaded type's *fields* when the type is loaded, so a mod can compile cleanly and
// still die at registration time with
//
//   TypeLoadException: Could not load type of field 'K2D2.UI.K2D2Window:_panel' (0) due to:
//   Could not resolve type with token 01000063 from typeref (expected class
//   'UnityEngine.UIElements.PanelRenderer' in assembly 'UnityEngine.UIElementsModule' ...)
//
// which is exactly how the released v1.1.0 binary failed on Redux 0.2.8.5. Compiling against the
// right assemblies prevents that, but nothing in the compiler proves the result: a stale reference
// assembly, an accidentally included newer DLL, or a hand-edited binary can all reintroduce it.
//
// This probe walks the built assembly's types and forces resolution of every declared field,
// property and method type, plus its base types, using the runtime assemblies that ship with the
// game. It is the same resolution the loader performs, so "PROBE OK" means the assembly's type
// graph is resolvable by this runtime. It is deliberately run under Mono on the SDK/CI side with
// the staged game assemblies - never inside the game.
//
// Exit code 0 = everything resolved, 1 = at least one unresolved item (details on stdout),
// 2 = bad usage.
internal static class ApiProbe
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("usage: ApiProbe <mod-assembly.dll>");
            return 2;
        }

        Assembly asm;
        try
        {
            asm = Assembly.LoadFrom(args[0]);
        }
        catch (Exception ex)
        {
            Console.WriteLine("FAIL could not load " + args[0] + " :: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }

        Console.WriteLine("Loaded: " + asm.FullName);

        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic |
                                  BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        int failures = 0;

        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            foreach (var le in ex.LoaderExceptions)
                Console.WriteLine("FAIL loader: " + le.Message);
            return 1;
        }

        foreach (var t in types)
        {
            try
            {
                // Base type chain: a missing base is just as fatal to the loader.
                for (var b = t.BaseType; b != null; b = b.BaseType) { }

                // Field types are the ones that produce the field-scoped TypeLoadException above.
                foreach (var f in t.GetFields(Flags))
                    if (f.FieldType == null)
                    {
                        Console.WriteLine("FAIL null field type: " + t.FullName + "." + f.Name);
                        failures++;
                    }

                foreach (var p in t.GetProperties(Flags)) { var _ = p.PropertyType; }
                foreach (var m in t.GetMethods(Flags)) { var _ = m.ReturnType; }
                foreach (var c in t.GetConstructors(Flags))
                    foreach (var cp in c.GetParameters()) { var _ = cp.ParameterType; }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + t.FullName + " :: " + ex.GetType().Name + ": " + ex.Message);
                failures++;
            }
        }

        // Name the types involved in the reported crash explicitly, so a regression is unmissable even
        // if it somehow does not surface through the sweep above.
        foreach (var name in new[] { "K2D2.UI.K2D2Window", "K2D2.K2D2_Plugin", "K2D2.Lift.FinalCircularize", "K2UI.K2AutoFitLabel" })
        {
            var found = asm.GetType(name) != null;
            Console.WriteLine((found ? "OK   " : "MISS ") + name);
            if (!found) failures++;
        }

        Console.WriteLine(failures == 0
            ? "PROBE OK: every type, field, property and method resolved against the runtime."
            : "PROBE FAILED: " + failures + " unresolved item(s).");
        return failures == 0 ? 0 : 1;
    }
}
