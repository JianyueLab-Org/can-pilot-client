using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CanClient.Core.Settings;

/// <summary>macOS Keychain / Linux Secret Service；密码不放进 JSON、日志或命令行参数。</summary>
public static class CredentialStore
{
    private const string Service = "com.jianyuelab.can-pilot";
    private const string Account = "network-password";
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
    [DllImport(Security)] private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, out uint length, out IntPtr data, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint length, byte[] password, out IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemModifyAttributesAndData(IntPtr item, IntPtr attributes, uint length, byte[] data);
    [DllImport(Security)] private static extern int SecKeychainItemDelete(IntPtr item);
    [DllImport(Security)] private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr item);

    public static string? Read() => Access(null, read: true);
    public static void Save(string? password) => Access(password, read: false);

    private static string? Access(string? password, bool read)
    {
        if (OperatingSystem.IsWindows())
        {
            if (read)
            {
                if (!CredRead(Service, 1, 0, out var pointer))
                {
                    if (Marshal.GetLastWin32Error() == 1168) return null;
                    throw new IOException("无法读取 Windows 凭据管理器。");
                }
                try
                {
                    var credential = Marshal.PtrToStructure<Credential>(pointer);
                    return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
                }
                finally { CredFree(pointer); }
            }
            if (password is null)
            {
                if (!CredDelete(Service, 1, 0) && Marshal.GetLastWin32Error() != 1168)
                    throw new IOException("无法删除 Windows 凭据。");
                return null;
            }
            var blob = Marshal.StringToCoTaskMemUni(password);
            try
            {
                var credential = new Credential { Type = 1, TargetName = Service, UserName = Account,
                    CredentialBlobSize = (uint)Encoding.Unicode.GetByteCount(password), CredentialBlob = blob, Persist = 2 };
                if (!CredWrite(ref credential, 0)) throw new IOException("无法保存 Windows 凭据。");
                return null;
            }
            finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
        }
        if (OperatingSystem.IsMacOS())
        {
            var service = Encoding.UTF8.GetBytes(Service);
            var account = Encoding.UTF8.GetBytes(Account);
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                out var length, out var data, out var item);
            try
            {
                if (read)
                {
                    if (status == -25300) return null;
                    if (status != 0) throw new IOException("无法读取系统钥匙串。");
                    var bytes = new byte[length];
                    Marshal.Copy(data, bytes, 0, bytes.Length);
                    return Encoding.UTF8.GetString(bytes);
                }
                if (password is null)
                {
                    if (status == 0 && SecKeychainItemDelete(item) != 0) throw new IOException("无法清除系统钥匙串。");
                    if (status != 0 && status != -25300) throw new IOException("无法访问系统钥匙串。");
                    return null;
                }
                var secret = Encoding.UTF8.GetBytes(password);
                if (status == 0) status = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)secret.Length, secret);
                else if (status == -25300) status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service,
                    (uint)account.Length, account, (uint)secret.Length, secret, out item);
                if (status != 0) throw new IOException("系统钥匙串拒绝保存密码。");
                return null;
            }
            finally
            {
                if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (item != IntPtr.Zero) CFRelease(item);
            }
        }
        if (OperatingSystem.IsLinux())
        {
            var start = new ProcessStartInfo("secret-tool") { RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in read ? new[] { "lookup", "service", Service, "account", Account }
                         : password is null ? new[] { "clear", "service", Service, "account", Account }
                         : new[] { "store", "--label=CAN Pilot network password", "service", Service, "account", Account })
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("无法启动系统凭据库。");
            if (!read && password is not null) process.StandardInput.Write(password);
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000)) { process.Kill(); throw new IOException("系统凭据库访问超时。"); }
            if (process.ExitCode != 0 && !(read && process.ExitCode == 1)) throw new IOException("系统凭据库不可用。");
            return read && process.ExitCode == 0 ? output.GetAwaiter().GetResult().TrimEnd('\r', '\n') : null;
        }
        // 未支持的平台不回退为明文。
        if (!read && password is not null) throw new IOException("当前平台尚未启用系统凭据保存。");
        return null;
    }
}
