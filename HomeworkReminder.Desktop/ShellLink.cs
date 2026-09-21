using System;
using System.Runtime.InteropServices;
using System.Text;

namespace HomeworkReminder.Desktop;

/// <summary>
/// 创建带 <c>System.AppUserModel.ID</c> 属性的 Windows 快捷方式（.lnk）。
/// <para>
/// 为什么需要它：未打包应用发 toast 时，通知平台要求该 AUMID 对应一个「已安装的应用」。
/// 实测只写 <c>HKCU\Software\Classes\AppUserModelId</c> 不够（<c>CreateToastNotifier</c>
/// 返回 <c>0x80070490</c>）；再补一个带 AppUserModelID 属性的开始菜单快捷方式即可满足。
/// </para>
/// <para>
/// 用 COM 互操作而非 Shell 自动化，是为了不引入对 <c>WScript.Shell</c> 的依赖。
/// </para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class ShellLink
{
    /// <summary>
    /// <c>PKEY_AppUserModel.ID</c> 的 FMTID（见 propkey.h 的 <c>System.AppUserModel.ID</c>）。
    /// 别与 <c>FMTID_ShellDetails</c>（28636AA6-…，pid 5 = System.ComputerName）混淆——
    /// 写错 FMTID 时属性根本落不到 AppUserModel.ID 上。
    /// </summary>
    private static readonly Guid FmtIdAppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    /// <summary><c>System.AppUserModel.ID</c> 的 PID（= 5）。</summary>
    private const int PidAppUserModelId = 5;

    private const int StgmRead = 0x00000000;

    public static void Create(string linkPath, string targetPath, string description, string aumid, string iconPath)
    {
        object? shellLinkObj = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))
                       ?? throw new InvalidOperationException("无法获取 ShellLink COM 类型");

            shellLinkObj = Activator.CreateInstance(type)
                           ?? throw new InvalidOperationException("无法创建 ShellLink 实例");

            var link = (IShellLinkW)shellLinkObj;
            link.SetPath(targetPath);
            link.SetDescription(description);
            link.SetIconLocation(iconPath, 0);
            link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(targetPath) ?? string.Empty);

            // 通过属性存储写入 System.AppUserModel.ID
            var store = (IPropertyStore)shellLinkObj;
            var key = new PropertyKey(FmtIdAppUserModel, PidAppUserModelId);
            var variant = Variant.FromString(aumid);
            try
            {
                var hr = store.SetValue(ref key, ref variant);
                // AUMID 写不上时这个快捷方式对通知平台毫无意义——失败必须可见，
                // 不能静默吞掉后让上层误以为注册成功。
                if (hr < 0)
                    throw Marshal.GetExceptionForHR(hr)
                          ?? new InvalidOperationException($"SetValue(AppUserModel.ID) 失败：0x{hr:X8}");
                store.Commit();
            }
            finally
            {
                variant.Clear();
            }

            var persist = (IPersistFile)shellLinkObj;
            persist.Save(linkPath, true);
        }
        finally
        {
            if (shellLinkObj is not null && Marshal.IsComObject(shellLinkObj))
                Marshal.FinalReleaseComObject(shellLinkObj);
        }
    }

    /// <summary>
    /// 读取现有快捷方式的目标路径。读不到（文件损坏、COM 失败等）时返回 false，
    /// 调用方据此重建快捷方式。
    /// </summary>
    public static bool TryGetTarget(string linkPath, out string target)
    {
        target = string.Empty;
        object? shellLinkObj = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            if (type is null) return false;

            shellLinkObj = Activator.CreateInstance(type);
            if (shellLinkObj is null) return false;

            var persist = (IPersistFile)shellLinkObj;
            persist.Load(linkPath, StgmRead);

            var link = (IShellLinkW)shellLinkObj;
            var sb = new StringBuilder(capacity: 1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            target = sb.ToString();
            return target.Length > 0;
        }
        catch (Exception)
        {
            target = string.Empty;
            return false;
        }
        finally
        {
            if (shellLinkObj is not null && Marshal.IsComObject(shellLinkObj))
                Marshal.FinalReleaseComObject(shellLinkObj);
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid formatId, int propertyId)
    {
        public Guid FormatId = formatId;
        public int PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr PointerValue;

        // VT_LPWSTR = 31
        public static Variant FromString(string value)
        {
            var v = new Variant { VariantType = 31, PointerValue = Marshal.StringToCoTaskMemUni(value) };
            return v;
        }

        public void Clear()
        {
            if (PointerValue != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(PointerValue);
                PointerValue = IntPtr.Zero;
            }
        }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int cProps);
        [PreserveSig] int GetAt(int iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out Variant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref Variant pv);
        [PreserveSig] int Commit();
    }
}
