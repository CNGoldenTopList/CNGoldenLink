using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CNGoldenLink;

internal interface ICredentialStore
{
    string? Load(Uri origin);
    void Save(Uri origin, string token);
    void Forget(Uri origin);
}

// OS credential stores keep tokens out of settings, save data, logs, and release archives.
internal static class CredentialStore
{
    private const int MaximumCredentialBytes = 4096;
    private static readonly ICredentialStore PlatformStore = CreatePlatformStore();

    private static string Target(Uri origin) => "CNGoldenLink/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin.AbsoluteUri)));

    public static string? Load(Uri origin) => PlatformStore.Load(origin);
    public static void Save(Uri origin, string token) => PlatformStore.Save(origin, token);
    public static void Forget(Uri origin) => PlatformStore.Forget(origin);

    private static ICredentialStore CreatePlatformStore() {
        if (OperatingSystem.IsWindows()) return new WindowsCredentialStore();
        if (OperatingSystem.IsMacOS()) return new MacKeychainStore();
        return new UnsupportedCredentialStore();
    }

    private sealed class UnsupportedCredentialStore : ICredentialStore
    {
        public string? Load(Uri origin) => throw Unsupported();
        public void Save(Uri origin, string token) => throw Unsupported();
        public void Forget(Uri origin) => throw Unsupported();
        private static PlatformNotSupportedException Unsupported() =>
            new("credential_store_unsupported");
    }

    private sealed class WindowsCredentialStore : ICredentialStore
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential {
            public uint Flags, Type;
            public string TargetName;
            public string? Comment;
            public long LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist, AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias, UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Read(string target, uint type, uint flags, out IntPtr credential);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Write(ref Credential credential, uint flags);
        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Delete(string target, uint type, uint flags);
        [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);

        public string? Load(Uri origin) {
            var target = Target(origin);
            if (!Read(target, 1, 0, out var pointer)) {
                if (Marshal.GetLastWin32Error() == 1168) return null;
                throw new IOException("credential_read_failed");
            }
            try {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                if (credential.CredentialBlobSize > MaximumCredentialBytes) throw new IOException("credential_invalid");
                return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            } finally { CredFree(pointer); }
        }

        public void Save(Uri origin, string token) {
            var target = Target(origin);
            var pointer = Marshal.StringToCoTaskMemUni(token);
            try {
                var credential = new Credential { Type = 1, TargetName = target, CredentialBlob = pointer,
                    CredentialBlobSize = (uint)Encoding.Unicode.GetByteCount(token), Persist = 2, UserName = "CNGoldenLink" };
                if (!Write(ref credential, 0)) throw new IOException("credential_write_failed");
            } finally { Marshal.ZeroFreeCoTaskMemUnicode(pointer); }
        }

        public void Forget(Uri origin) {
            var target = Target(origin);
            if (!Delete(target, 1, 0) && Marshal.GetLastWin32Error() != 1168)
                throw new IOException("credential_delete_failed");
        }
    }

    private sealed class MacKeychainStore : ICredentialStore
    {
        private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
        private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const string Service = "CNGoldenLink";
        private const int Success = 0;
        private const int DuplicateItem = -25299;
        private const int ItemNotFound = -25300;
        private const uint Utf8Encoding = 0x08000100;
        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        private static readonly IntPtr SecurityHandle = NativeLibrary.Load(SecurityFramework);
        private static readonly IntPtr CoreFoundationHandle = NativeLibrary.Load(CoreFoundationFramework);
        private static readonly IntPtr SecClass = SecurityConstant("kSecClass");
        private static readonly IntPtr SecClassGenericPassword = SecurityConstant("kSecClassGenericPassword");
        private static readonly IntPtr SecAttrService = SecurityConstant("kSecAttrService");
        private static readonly IntPtr SecAttrAccount = SecurityConstant("kSecAttrAccount");
        private static readonly IntPtr SecValueData = SecurityConstant("kSecValueData");
        private static readonly IntPtr SecReturnData = SecurityConstant("kSecReturnData");
        private static readonly IntPtr SecMatchLimit = SecurityConstant("kSecMatchLimit");
        private static readonly IntPtr SecMatchLimitOne = SecurityConstant("kSecMatchLimitOne");
        private static readonly IntPtr True = CoreFoundationConstant("kCFBooleanTrue");
        private static readonly IntPtr DictionaryKeyCallbacks = CoreFoundationSymbol("kCFTypeDictionaryKeyCallBacks");
        private static readonly IntPtr DictionaryValueCallbacks = CoreFoundationSymbol("kCFTypeDictionaryValueCallBacks");

        [DllImport(SecurityFramework)]
        private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
        [DllImport(SecurityFramework)]
        private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
        [DllImport(SecurityFramework)]
        private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);
        [DllImport(SecurityFramework)]
        private static extern int SecItemDelete(IntPtr query);

        [DllImport(CoreFoundationFramework)]
        private static extern IntPtr CFStringCreateWithBytes(IntPtr allocator, byte[] bytes, nint length,
            uint encoding, byte isExternalRepresentation);
        [DllImport(CoreFoundationFramework)]
        private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
        [DllImport(CoreFoundationFramework)]
        private static extern nint CFDataGetLength(IntPtr data);
        [DllImport(CoreFoundationFramework)]
        private static extern IntPtr CFDataGetBytePtr(IntPtr data);
        [DllImport(CoreFoundationFramework)]
        private static extern nuint CFGetTypeID(IntPtr value);
        [DllImport(CoreFoundationFramework)]
        private static extern nuint CFDataGetTypeID();
        [DllImport(CoreFoundationFramework)]
        private static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] values,
            nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);
        [DllImport(CoreFoundationFramework)]
        private static extern void CFRelease(IntPtr value);

        public string? Load(Uri origin) {
            var account = Target(origin);
            var query = CreateQuery(account, true, "credential_read_failed");
            IntPtr result = IntPtr.Zero;
            try {
                int status = SecItemCopyMatching(query, out result);
                if (status == ItemNotFound) return null;
                if (status != Success) throw new IOException("credential_read_failed");
                if (result == IntPtr.Zero || CFGetTypeID(result) != CFDataGetTypeID())
                    throw new IOException("credential_invalid");
                nint nativeLength = CFDataGetLength(result);
                if (nativeLength < 0 || nativeLength > MaximumCredentialBytes)
                    throw new IOException("credential_invalid");
                int length = (int)nativeLength;
                var bytes = new byte[length];
                try {
                    if (length > 0) {
                        var pointer = CFDataGetBytePtr(result);
                        if (pointer == IntPtr.Zero) throw new IOException("credential_invalid");
                        Marshal.Copy(pointer, bytes, 0, length);
                    }
                    try { return StrictUtf8.GetString(bytes); }
                    catch (DecoderFallbackException) { throw new IOException("credential_invalid"); }
                } finally { CryptographicOperations.ZeroMemory(bytes); }
            } finally {
                Release(result);
                Release(query);
            }
        }

        public void Save(Uri origin, string token) {
            var account = Target(origin);
            byte[] bytes;
            try { bytes = StrictUtf8.GetBytes(token); }
            catch (EncoderFallbackException) { throw new IOException("credential_invalid"); }
            try {
                if (bytes.Length > MaximumCredentialBytes) throw new IOException("credential_invalid");
                var data = CreateData(bytes, "credential_write_failed");
                IntPtr query = IntPtr.Zero, update = IntPtr.Zero, add = IntPtr.Zero;
                try {
                    query = CreateQuery(account, false, "credential_write_failed");
                    update = CreateDictionary([SecValueData], [data], "credential_write_failed");
                    int status = SecItemUpdate(query, update);
                    if (status == ItemNotFound) {
                        add = CreateAdd(account, data, "credential_write_failed");
                        status = SecItemAdd(add, IntPtr.Zero);
                        if (status == DuplicateItem) status = SecItemUpdate(query, update);
                    }
                    if (status != Success) throw new IOException("credential_write_failed");
                } finally {
                    Release(add);
                    Release(update);
                    Release(query);
                    Release(data);
                }
            } finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        public void Forget(Uri origin) {
            var account = Target(origin);
            var query = CreateQuery(account, false, "credential_delete_failed");
            try {
                int status = SecItemDelete(query);
                if (status != Success && status != ItemNotFound) throw new IOException("credential_delete_failed");
            } finally { Release(query); }
        }

        private static IntPtr CreateQuery(string account, bool returnData, string error) {
            var service = CreateString(Service, error);
            IntPtr accountValue = IntPtr.Zero;
            try {
                accountValue = CreateString(account, error);
                if (returnData)
                    return CreateDictionary(
                        [SecClass, SecAttrService, SecAttrAccount, SecReturnData, SecMatchLimit],
                        [SecClassGenericPassword, service, accountValue, True, SecMatchLimitOne], error);
                return CreateDictionary([SecClass, SecAttrService, SecAttrAccount],
                    [SecClassGenericPassword, service, accountValue], error);
            } finally {
                Release(accountValue);
                Release(service);
            }
        }

        private static IntPtr CreateAdd(string account, IntPtr data, string error) {
            var service = CreateString(Service, error);
            IntPtr accountValue = IntPtr.Zero;
            try {
                accountValue = CreateString(account, error);
                return CreateDictionary([SecClass, SecAttrService, SecAttrAccount, SecValueData],
                    [SecClassGenericPassword, service, accountValue, data], error);
            } finally {
                Release(accountValue);
                Release(service);
            }
        }

        private static IntPtr CreateString(string value, string error) {
            var bytes = Encoding.UTF8.GetBytes(value);
            var result = CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, Utf8Encoding, 0);
            return result != IntPtr.Zero ? result : throw new IOException(error);
        }

        private static IntPtr CreateData(byte[] bytes, string error) {
            var result = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
            return result != IntPtr.Zero ? result : throw new IOException(error);
        }

        private static IntPtr CreateDictionary(IntPtr[] keys, IntPtr[] values, string error) {
            var result = CFDictionaryCreate(IntPtr.Zero, keys, values, keys.Length,
                DictionaryKeyCallbacks, DictionaryValueCallbacks);
            return result != IntPtr.Zero ? result : throw new IOException(error);
        }

        private static IntPtr SecurityConstant(string name) =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityHandle, name));
        private static IntPtr CoreFoundationConstant(string name) =>
            Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreFoundationHandle, name));
        private static IntPtr CoreFoundationSymbol(string name) => NativeLibrary.GetExport(CoreFoundationHandle, name);
        private static void Release(IntPtr value) { if (value != IntPtr.Zero) CFRelease(value); }
    }
}
