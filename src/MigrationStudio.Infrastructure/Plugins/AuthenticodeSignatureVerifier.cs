using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace MigrationStudio.Infrastructure.Plugins;

internal static class AuthenticodeSignatureVerifier
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");

    public static void Verify(string assemblyPath, IReadOnlySet<string> trustedPublisherThumbprints)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Authenticode plugin verification is supported only on Windows.");
        }

        var fileInfo = new WinTrustFileInfo(assemblyPath);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePointer, false);
            var trustData = new WinTrustData(filePointer);
            var action = GenericVerifyV2;
            var result = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
            if (result != 0)
            {
                throw new InvalidDataException(
                    $"The plugin Authenticode signature is invalid ({new Win32Exception(result).Message}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(filePointer);
        }

        if (trustedPublisherThumbprints.Count == 0)
        {
            return;
        }

        using var certificate = new X509Certificate2(
            X509Certificate.CreateFromSignedFile(assemblyPath));
        var normalized = NormalizeThumbprint(certificate.Thumbprint);
        if (!trustedPublisherThumbprints.Contains(normalized))
        {
            throw new InvalidDataException(
                $"The plugin publisher certificate '{normalized}' is not trusted by configuration.");
        }
    }

    public static HashSet<string> NormalizeThumbprints(IEnumerable<string> thumbprints) =>
        thumbprints
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeThumbprint)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeThumbprint(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr windowHandle,
        ref Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo
    {
        public WinTrustFileInfo(string filePath)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = filePath;
        }

        private readonly uint StructureSize;

        [MarshalAs(UnmanagedType.LPWStr)]
        private readonly string FilePath;

        private readonly IntPtr FileHandle = IntPtr.Zero;

        private readonly IntPtr KnownSubject = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public WinTrustData(IntPtr fileInfo)
        {
            StructureSize = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2;
            RevocationChecks = 0;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00001000;
            UiContext = 0;
            SignatureSettings = IntPtr.Zero;
        }

        private uint StructureSize;
        private IntPtr PolicyCallbackData;
        private IntPtr SipClientData;
        private uint UiChoice;
        private uint RevocationChecks;
        private uint UnionChoice;
        private IntPtr FileInfo;
        private uint StateAction;
        private IntPtr StateData;
        private IntPtr UrlReference;
        private uint ProviderFlags;
        private uint UiContext;
        private IntPtr SignatureSettings;
    }
}
