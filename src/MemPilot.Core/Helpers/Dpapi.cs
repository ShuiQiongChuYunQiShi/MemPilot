using System.Runtime.InteropServices;
using System.Text;

namespace MemPilot.Helpers;

/// <summary>
/// Windows DPAPI 加解密封装（v0.3.8，棱镜 security/network）：联网查询的 API 密钥不能再明文躺在 config.json。
/// 用 CryptProtectData / CryptUnprotectData（CurrentUser 作用域），只有当前用户能解开；不引入 NuGet 包。
///
/// 存储格式：`dpapi:` + Base64(密文)。读取时没有前缀就按明文处理（老配置平滑迁移，下次保存自动加密）。
/// 失败时一律降级为原值——密钥丢了功能就废了，宁可退回明文也不能让程序起不来。
/// </summary>
public static class Dpapi
{
    private const string Prefix = "dpapi:";
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public static string Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(plain);
            var input = new DATA_BLOB { cbData = bytes.Length, pbData = Marshal.AllocHGlobal(bytes.Length) };
            try
            {
                Marshal.Copy(bytes, 0, input.pbData, bytes.Length);
                if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output))
                    return plain; // 降级：加密失败就存明文，功能优先
                try
                {
                    var cipher = new byte[output.cbData];
                    Marshal.Copy(output.pbData, cipher, 0, output.cbData);
                    return Prefix + Convert.ToBase64String(cipher);
                }
                finally { LocalFree(output.pbData); }
            }
            finally { Marshal.FreeHGlobal(input.pbData); }
        }
        catch { return plain; }
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // 老配置：明文
        try
        {
            var cipher = Convert.FromBase64String(stored[Prefix.Length..]);
            var input = new DATA_BLOB { cbData = cipher.Length, pbData = Marshal.AllocHGlobal(cipher.Length) };
            try
            {
                Marshal.Copy(cipher, 0, input.pbData, cipher.Length);
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output))
                    return ""; // 解不开（换用户/换机器）：当作没有密钥，让用户重填
                try
                {
                    var plain = new byte[output.cbData];
                    Marshal.Copy(output.pbData, plain, 0, output.cbData);
                    return Encoding.UTF8.GetString(plain);
                }
                finally { LocalFree(output.pbData); }
            }
            finally { Marshal.FreeHGlobal(input.pbData); }
        }
        catch { return ""; }
    }

    /// <summary>是否已加密（诊断/测试用）。</summary>
    public static bool IsProtected(string? value)
        => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);
}
