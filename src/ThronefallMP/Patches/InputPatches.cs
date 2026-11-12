using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Rewired;
using UnityEngine;

namespace ThronefallMP;

/// Two windows on one Mac both run (runInBackground) and Rewired keeps reading the keyboard
/// for the unfocused one. In MP the unfocused window must see no input at all, so the game's
/// reads on Rewired.Player (GetAxis*/GetButton*/GetNegativeButton*, by name or id) answer
/// "nothing pressed". Test flags drive the game from code and never go through Player.
static class FocusInput
{
    internal static bool Blocked => Plugin.Instance != null && Plugin.Instance.role != "off" && !Active;

    /// Is this process the frontmost app? Unity's Application.isFocused gets stuck when two
    /// instances share one .app bundle on macOS, so ask AppKit. Cached once per frame.
    internal static bool Active
    {
        get
        {
            if (frame == Time.frameCount) return cached;
            frame = Time.frameCount;
            cached = Query();
            return cached;
        }
    }

    static int frame = -1;
    static bool cached = true, macFailed;

    static bool Query()
    {
        if (Application.platform != RuntimePlatform.OSXPlayer || macFailed) return Application.isFocused;
        try
        {
            var app = objc_msgSend(objc_getClass("NSRunningApplication"), sel_registerName("currentApplication"));
            return app == IntPtr.Zero || objc_msgSend_bool(app, sel_registerName("isActive")) != 0;
        }
        catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
        {
            macFailed = true; // never retry; fall back to Unity's flag
            Plugin.Instance?.Log("AppKit focus query unavailable, using Application.isFocused");
            return Application.isFocused;
        }
    }

    const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern byte objc_msgSend_bool(IntPtr receiver, IntPtr selector);

    internal static IEnumerable<MethodBase> Methods(System.Type returns) =>
        typeof(Player).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == returns && !m.Name.Contains("Maps")
                && (m.Name.StartsWith("GetAxis") || m.Name.StartsWith("GetButton") || m.Name.StartsWith("GetNegativeButton"))
                && m.GetParameters().Length > 0 && (m.GetParameters()[0].ParameterType == typeof(string) || m.GetParameters()[0].ParameterType == typeof(int)))
            .Cast<MethodBase>();
}

[HarmonyPatch]
static class FocusButtonPatch
{
    static IEnumerable<MethodBase> TargetMethods() => FocusInput.Methods(typeof(bool));
    static bool Prefix(ref bool __result) { if (!FocusInput.Blocked) return true; __result = false; return false; }
}

[HarmonyPatch]
static class FocusAxisPatch
{
    static IEnumerable<MethodBase> TargetMethods() => FocusInput.Methods(typeof(float));
    static bool Prefix(ref float __result) { if (!FocusInput.Blocked) return true; __result = 0f; return false; }
}
