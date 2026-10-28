using System.Runtime.InteropServices;

namespace MemPilot.Services;

/// <summary>
/// Full Authenticode verification via WinVerifyTrust. Unlike X509Certificate.CreateFromSignedFile
/// (which merely parses any cert blob, including self-signed ones), this validates the chain
/// against trusted roots — a certificate with Subject "Microsoft" forged locally will fail here.
/// Results are cached per (path, lastWriteTime).
///
/// NOTE: WinVerifyTrust behaves differently across environments (works from desktop sessions,
/// may fail on CI runners) — VerifyDetailed exposes the raw status for diagnostics, and
/// LastDiag records the most recent status for integration-test dumps.
/// </summary>
public class TrustVerifier
{
    private static readonly Guid WintrustActionGenericVerifyV2 = new("00C563FB-7BA5-4AD6-B3C7-C737F60ABC5D");
    private static readonly Dictionary<string, (DateTime lastWrite, bool verified, string subject, string issuer)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    public static volatile string LastDiag = "none";

    public (bool verified, string subject, string issuer) Verify(string exePath)
    {
        var r = VerifyDetailed(exePath);
        return (r.verified, r.subject, r.issuer);
    }

    public (bool verified, string subject, string issuer, int status, string note) VerifyDetailed(string exePath)
    {
        try
        {
            if (string.IsNullOrEmpty(exePath) || !System.IO.File.Exists(exePath))
                return (false, "", "", -2, "file missing");

            var lastWrite = System.IO.File.GetLastWriteTimeUtc(exePath);
            lock (_cache)
            {
                if (_cache.TryGetValue(exePath, out var cached) && cached.lastWrite == lastWrite)
                    return (cached.verified, cached.subject, cached.issuer, 0, "cache");
            }

            var fi = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = exePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };
            var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            var wtDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            try
            {
                Marshal.StructureToPtr(fi, fileInfoPtr, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    pPolicyCallbackData = IntPtr.Zero,
                    pSIPClientData = IntPtr.Zero,
                    dwUIChoice = WTD_UI_NONE,              // 2: never show UI
                    fdwRevocationChecks = WTD_REVOKE_NONE, // 0: offline friendly
                    dwUnionChoice = WTD_CHOICE_FILE,       // 1
                    pFile = fileInfoPtr,
                    dwStateAction = WTD_STATEACTION_VERIFY,
                    hWVTStateData = IntPtr.Zero,
                    pWizardData = IntPtr.Zero,
                    dwPadReserved1 = 0,
                    dwUIContext = 0
                };
                Marshal.StructureToPtr(data, wtDataPtr, false);
                int status = WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, wtDataPtr);

                data = Marshal.PtrToStructure<WINTRUST_DATA>(wtDataPtr);
                data.dwStateAction = WTD_STATEACTION_CLOSE;
                Marshal.StructureToPtr(data, wtDataPtr, false);
                WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, wtDataPtr);

                bool verified = status == 0; // S_OK / ERROR_SUCCESS
                string subject = "", issuer = "";
                if (verified)
                {
                    try
                    {
#pragma warning disable SYSLIB0057
                        var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(exePath);
#pragma warning restore SYSLIB0057
                        subject = cert.Subject;
                        issuer = cert.Issuer;
                    }
                    catch { }
                }
                lock (_cache)
                {
                    if (_cache.Count > 4096) _cache.Clear();
                    _cache[exePath] = (lastWrite, verified, subject, issuer);
                }
                return (verified, subject, issuer, status, "wintrust");
            }
            finally
            {
                Marshal.FreeHGlobal(fileInfoPtr);
                Marshal.FreeHGlobal(wtDataPtr);
            }
        }
        catch (Exception ex)
        {
            LastDiag = "exception: " + ex.GetType().Name + ": " + ex.Message;
            return (false, "", "", -1, LastDiag);
        }
    }

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern int WinVerifyTrust(IntPtr hWnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pWizardData;
        public uint dwPadReserved1;
        public uint dwUIContext;
        public IntPtr pSignatureSettings; // Win8+
        public uint dwProvFlags;
        public uint dwUIContext2;
    }
}
