using System;
using System.Collections.Generic;
using System.Reflection;
using API.Assembly;
using API.Events;
using HarmonyLib;
using Logger = Carbon.Logger;
using Object = UnityEngine.Object;

namespace RustServerMetrics;

public class RustServerMetricsLoader : IModulePackage
{
    public static bool __serverStarted = false;
    
    public static Harmony __harmonyInstance;
    public static List<Harmony> __modTimeWarningsHarmonyInstances = new ();
    private static readonly List<ModTimeWarningPatchSet> __modTimeWarningPatchSets = new();

    private sealed class ModTimeWarningPatchSet
    {
        public ModTimeWarningPatchSet(Harmony harmony, List<MethodInfo> methods)
        {
            Harmony = harmony;
            Methods = methods;
        }

        public Harmony Harmony { get; }
        public List<MethodInfo> Methods { get; }
        public bool Applied { get; set; }
    }
    
    public void AddModTimeWarnings(List<MethodInfo> methods)
    {
        var methodList = methods == null ? new List<MethodInfo>() : new List<MethodInfo>(methods);
        var instance = new Harmony($"RustServerMetrics.ModTimeWarnings.{__modTimeWarningsHarmonyInstances.Count}");
        var patchSet = new ModTimeWarningPatchSet(instance, methodList);

        __modTimeWarningsHarmonyInstances.Add(instance);
        __modTimeWarningPatchSets.Add(patchSet);

        foreach (var method in methodList)
        {
            Logger.Log($"{method.DeclaringType?.Name}.{method.Name}");
        }

        if (__serverStarted)
        {
            Logger.Log(ApplyModTimeWarningPatchSet(patchSet)
                ? $"[ServerMetrics]: Added {methodList.Count} ModTimeWarnings"
                : $"[ServerMetrics]: Failed to add {methodList.Count} ModTimeWarnings");
            return;
        }

        Logger.Log($"[ServerMetrics]: Queued {methodList.Count} ModTimeWarnings for server start");
    }

    internal static void ApplyPendingModTimeWarnings()
    {
        foreach (var patchSet in __modTimeWarningPatchSets)
        {
            if (patchSet.Applied || !ApplyModTimeWarningPatchSet(patchSet))
            {
                continue;
            }

            Logger.Log($"[ServerMetrics]: Added {patchSet.Methods.Count} queued ModTimeWarnings");
        }
    }

    private static bool ApplyModTimeWarningPatchSet(ModTimeWarningPatchSet patchSet)
    {
        ModTimeWarnings.Methods.Clear();
        ModTimeWarnings.Methods.AddRange(patchSet.Methods);

        var patchProcessor = new PatchClassProcessor(patchSet.Harmony, typeof(ModTimeWarnings));
        patchSet.Applied = patchProcessor.Patch() != null;
        return patchSet.Applied;
    }

    public void Awake(EventArgs args)
    {
	    Logger.Log($"[ServerMetrics]: Carbon Community version {typeof(RustServerMetricsLoader).Assembly.GetName().Version} [module]");
    }

    public void OnLoaded(EventArgs args)
    {
        if (!Bootstrap.bootstrapInitRun)
            return;
        
	    Carbon.Community.Runtime.Events.Subscribe(CarbonEvent.HookValidatorRefreshed, _ =>
	    {
		    Carbon.Components.Harmony.PatchAll(Assembly.GetExecutingAssembly());

        MetricsLogger.Initialize();
	    });
    }

    public void OnUnloaded(EventArgs args)
    {
        __harmonyInstance?.UnpatchAll();
        foreach (var instance in __modTimeWarningsHarmonyInstances)
        {
            instance?.UnpatchAll();
        }
        
        if (MetricsLogger.Instance != null)
            Object.DestroyImmediate(MetricsLogger.Instance);
    }
}
