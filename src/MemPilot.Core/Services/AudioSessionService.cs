using System.Runtime.InteropServices;

namespace MemPilot.Services;

/// <summary>
/// Detects which PIDs are actively producing sound right now, via WASAPI per-session peak meters.
/// Used by the unused-detector so a process playing music in the background is never treated
/// as "unused". Degrades gracefully: if COM/audio is unavailable, returns an empty set.
/// </summary>
public class AudioSessionService
{
    /// <summary>Returns the set of PIDs whose audio sessions currently have a non-zero peak,
    /// plus (weaker) PIDs owning active sessions if meter QI is unsupported.</summary>
    public HashSet<int> GetActiveAudioPids()
    {
        var pids = new HashSet<int>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out var device) != 0 || device == null)
                return pids;
            var iid = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"); // IID_IAudioSessionManager2
            if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var mgrObj) != 0 || mgrObj is not IAudioSessionManager2 mgr)
                return pids;
            if (mgr.GetSessionEnumerator(out var sessionEnum) != 0 || sessionEnum == null)
                return pids;
            if (sessionEnum.GetCount(out var count) != 0) return pids;
            for (int i = 0; i < count; i++)
            {
                try
                {
                    if (sessionEnum.GetSession(i, out var ctl) != 0 || ctl == null) continue;
                    if (ctl.GetState(out var state) != 0) continue;
                    bool sessionActive = state == AudioSessionStateActive;
                    if (ctl is not IAudioSessionControl2 ctl2) continue;
                    if (ctl2.GetProcessId(out var pid) != 0 || pid == 0) continue;
                    if (!sessionActive) continue;

                    // Prefer a real peak reading; fall back to "session is active".
                    bool hasSound = false;
                    if (ctl is IAudioMeterInformation meter)
                    {
                        try { hasSound = meter.GetPeakValue(out var peak) == 0 && peak > 0.0005f; }
                        catch { hasSound = false; }
                    }
                    if (hasSound || !HasMeterSupport) pids.Add((int)pid);
                }
                catch { }
            }
        }
        catch
        {
            // COM not available / no audio device / remote session — degrade silently.
        }
        return pids;
    }

    private static bool HasMeterSupport = true; // flipped off once if meter QI never works

    private const int DataFlowRender = 0;   // eRender
    private const int RoleConsole = 0;      // eConsole
    private const int ClsCtxAll = 23;       // CLSCTX_INPROC_SERVER | LOCAL_SERVER
    private const int AudioSessionStateActive = 1;

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig] int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
        // IAudioSessionManager2
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, [MarshalAs(UnmanagedType.Interface)] out IAudioSessionControl session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl (9 methods)
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, IntPtr eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid overrideValue, IntPtr eventContext);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        // IAudioSessionControl2
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference(int optOut);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
