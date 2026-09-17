using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HomeworkReminder.Services;

/// <summary>
/// Windows DPAPI（<c>CryptProtectData</c> / <c>CryptUnprotectData</c>，crypt32.dll）的
/// P/Invoke 封装，语义等价于
/// <c>System.Security.Cryptography.ProtectedData</c> + <c>DataProtectionScope.CurrentUser</c>：
/// 只有同一台机器上的同一 Windows 用户能解密，拷走文件也没用。
/// <para>
/// 不用 NuGet 包的原因：<c>System.Security.Cryptography.ProtectedData</c> 不在本仓库的
/// 离线包源（packages/）与机器缓存中，restore 拿不到（NU1101）。
/// 这两个 API 自 Windows 2000 起就内置在 crypt32.dll 里，P/Invoke 无任何额外依赖。
/// </para>
/// <para>
/// 仅 Windows 可用；调用方必须先用 <see cref="OperatingSystem.IsWindows()"/> 守卫，
/// 其他平台走自己的回落路径。
/// </para>
/// </summary>
internal static class Dpapi
{
    // 加密过程不弹任何 UI 对话框（后台保存会话时不能打断用户）。
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        IntPtr szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    /// <summary>加密。<see cref="Win32Exception"/>：DPAPI 调用失败。</summary>
    public static byte[] Protect(byte[] plain)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 仅在 Windows 上可用");
        if (plain is null) throw new ArgumentNullException(nameof(plain));

        var input = ToBlob(plain, out var inputPtr);
        try
        {
            if (!CryptProtectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptProtectData 调用失败");
            return FromBlob(output);
        }
        finally
        {
            Marshal.FreeHGlobal(inputPtr);
        }
    }

    /// <summary>
    /// 解密。<see cref="Win32Exception"/>：数据损坏、不是本用户加密的、或根本不是 DPAPI 密文。
    /// </summary>
    public static byte[] Unprotect(byte[] protectedData)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI 仅在 Windows 上可用");
        if (protectedData is null) throw new ArgumentNullException(nameof(protectedData));

        var input = ToBlob(protectedData, out var inputPtr);
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CryptUnprotectData 调用失败");
            return FromBlob(output);
        }
        finally
        {
            Marshal.FreeHGlobal(inputPtr);
        }
    }

    private static DataBlob ToBlob(byte[] data, out IntPtr ptr)
    {
        ptr = Marshal.AllocHGlobal(Math.Max(data.Length, 1));
        if (data.Length > 0) Marshal.Copy(data, 0, ptr, data.Length);
        return new DataBlob { cbData = data.Length, pbData = ptr };
    }

    private static byte[] FromBlob(DataBlob blob)
    {
        try
        {
            var result = new byte[blob.cbData];
            if (blob.cbData > 0) Marshal.Copy(blob.pbData, result, 0, blob.cbData);
            return result;
        }
        finally
        {
            // DPAPI 的输出缓冲区由系统分配，必须用 LocalFree 释放。
            LocalFree(blob.pbData);
        }
    }
}
