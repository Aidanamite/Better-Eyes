using UnityEngine;
using RaftModLoader;
using HMLLibrary;
using HarmonyLib;
using System.Collections.Concurrent;
using UnityEngine.AzureSky;
using BetterEyes;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Reflection;
using System;
using System.Runtime.CompilerServices;
using System.Globalization;

namespace BetterEyes
{
    public class Main : Mod
    {
        Harmony h;
        public static ConcurrentQueue<LODGroup> pending = new ConcurrentQueue<LODGroup>();
        static Camera lastMain;

        static float unmodifedRendDist;

        public static float RenderDistance = -1;
        public static float LODDistance = 1;
        public static float FogDistance = 1;
        public static bool FullLook = false;
        public void Start()
        {
            foreach (var l in Resources.FindObjectsOfTypeAll<LODGroup>())
                ModifyLODs(l);
            (h = new Harmony("com.aidanamite.BetterEyes")).PatchAll();
            Log("Mod has been loaded!");
        }

        public void Update()
        {
            while (pending.TryDequeue(out var l))
                ModifyLODs(l);
            TryUpdate(ref lastMain, Helper.MainCamera, (a, b) =>
            {
                if (a)
                    a.farClipPlane = unmodifedRendDist;
                if (b)
                {
                    unmodifedRendDist = b.farClipPlane;
                    if (RenderDistance >= 0)
                        b.farClipPlane = RenderDistance;
                }
            });
        }

        public void OnModUnload()
        {
            h?.UnpatchAll(h.Id);
            if (lastMain)
                lastMain.farClipPlane = unmodifedRendDist;
            if (LODDistance != 1)
            {
                var p = LODDistance;
                LODDistance = 1;
                foreach (var l in Resources.FindObjectsOfTypeAll<LODGroup>())
                    ModifyLODs(l, p);
            }
            Log("Mod has been unloaded!");
        }

        public static void ModifyLODs(LODGroup group, float? unmodifyAmount = null)
        {
            if (!group)
                return;
            LOD[] lods = group.GetLODs();
            for (int l = 0; l < lods.Length; l++)
            {
                if (unmodifyAmount != null)
                    lods[l].screenRelativeTransitionHeight *= unmodifyAmount.Value;
                lods[l].screenRelativeTransitionHeight /= LODDistance;
            }
            group.SetLODs(lods);
        }

        void ExtraSettingsAPI_Load() => ExtraSettingsAPI_SettingsClose();
        void ExtraSettingsAPI_SettingsClose()
        {
            FogDistance = ExtraSettingsAPI_GetInputValue("fogDist").ParseFloat();
            TryUpdate(ref LODDistance, Math.Min(ExtraSettingsAPI_GetInputValue("lodDist").ParseFloat(), 0.1f), (a, b) =>
            {
                foreach (var l in FindObjectsOfType<LODGroup>())
                    ModifyLODs(l,a);
            });
            if (TryUpdate(ref RenderDistance, ExtraSettingsAPI_GetInputValue("rendDist").ParseFloat(-1)) && lastMain)
                lastMain.farClipPlane = RenderDistance < 0 ? unmodifedRendDist : RenderDistance;
            FullLook = ExtraSettingsAPI_GetCheckboxState("fullLook");
        }

        public static bool TryUpdate<T>(ref T memory, T current, Action<T,T> BeforeUpdate = null)
        {
            if (memory == null ? current == null : memory.Equals(current))
                return false;
            BeforeUpdate?.Invoke(memory, current);
            memory = current;
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        string ExtraSettingsAPI_GetInputValue(string SettingName) => null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        bool ExtraSettingsAPI_GetCheckboxState(string SettingName) => false;
    }

    [HarmonyPatch(typeof(LODGroup),MethodType.Constructor)]
    static class Patch_CreateLODGroup
    {
        static void Postfix(LODGroup __instance) => Main.pending.Enqueue(__instance);
    }

    [HarmonyPatch(typeof(AzureSkyController))]
    static class Patch_AzureSkyController
    {
        [HarmonyPatch("Update")]
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            code.InsertRange(
                code.FindIndex(x => x.opcode == OpCodes.Stfld && x.operand is FieldInfo f && f.Name == "fogDistance") + 1,
                new[]
                {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(Patch_AzureSkyController),nameof(Postfix)))
                });
            return code;
        }
        [HarmonyPatch("BlendWeatherProfiles")]
        static void Postfix(AzureSkyController __instance)
        {
            if (Main.FogDistance < 0)
                __instance.fogDistance = -1f;
            else
                __instance.fogDistance *= Main.FogDistance;
        }
    }

    [HarmonyPatch]
    static class Patch_LookYMinMax
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MouseLook), "SetTargetRotYToCurrentRotation");
            yield return AccessTools.Method(typeof(MouseLook), "Update");
            yield return AccessTools.Method(typeof(PersonController), "GroundControll");
            yield break;
        }
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            for (int i = code.Count - 1; i >= 0; i--)
                if (code[i].opcode == OpCodes.Ldfld && code[i].operand is FieldInfo f)
                {
                    if (f.Name == "minimumY")
                        code.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch_LookYMinMax), nameof(EditMin))));
                    else if (f.Name == "maximumY")
                        code.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Patch_LookYMinMax), nameof(EditMax))));
                }
            return code;
        }
        static float EditMin(float original) => Main.FullLook ? -90 : original;
        static float EditMax(float original) => Main.FullLook ? 90 : original;
    }

    public static class ExtentionMethods
    {
        public static float ParseFloat(this string value, float EmptyFallback = 1) => value.ParseNFloat() ?? EmptyFallback;
        public static float? ParseNFloat(this string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            if (value.Contains(",") && !value.Contains("."))
                value = value.Replace(',', '.');
            var c = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfoByIetfLanguageTag("en-NZ");
            Exception e = null;
            float r = 0;
            try
            {
                r = float.Parse(value);
            }
            catch (Exception e2)
            {
                e = e2;
            }
            CultureInfo.CurrentCulture = c;
            if (e != null)
                throw e;
            return r;
        }
    }
}