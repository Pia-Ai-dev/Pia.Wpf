using System.Runtime.InteropServices;

namespace Pia.Native;

internal static unsafe partial class WinTrust
{
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_IGNORE = 0;
    // Revocation needs the CA's CRL endpoints, which some corporate networks block; chain and timestamp still count.
    private const uint WTD_REVOCATION_CHECK_NONE = 0x10;

    private const uint CRYPTCAT_OPEN_EXISTING = 2;
    private const uint CRYPTCAT_VERSION_2 = 2;
    private const uint X509_ASN_ENCODING_AND_PKCS_7 = 0x00010001;

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public char* pcwszFilePath;
        public nint hFile;
        public Guid* pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public WINTRUST_FILE_INFO* pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public char* pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    [LibraryImport("wintrust.dll")]
    private static partial int WinVerifyTrust(nint hwnd, Guid* pgActionID, WINTRUST_DATA* pWVTData);

    [LibraryImport("wintrust.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CryptCATOpen(string pwszFileName, uint fdwOpenFlags, nint hProv, uint dwPublicVersion, uint dwEncodingType);

    [LibraryImport("wintrust.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CryptCATGetMemberInfo(nint hCatalog, string pwszReferenceTag);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATClose(nint hCatalog);

    /// <summary>True when the file's own Authenticode signature (a .cat included) chains to a trusted root.</summary>
    public static bool HasValidSignature(string path)
    {
        fixed (char* filePath = path)
        {
            var file = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                pcwszFilePath = filePath,
            };
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WINTRUST_DATA),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = &file,
                dwStateAction = WTD_STATEACTION_IGNORE,
                dwProvFlags = WTD_REVOCATION_CHECK_NONE,
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(-1, &action, &data) == 0;
        }
    }

    /// <summary>True when the catalog lists a member tagged with <paramref name="sha256Hex"/>, the tag New-FileCatalog writes.</summary>
    public static bool CatalogHasMember(string catalogPath, string sha256Hex)
    {
        var catalog = CryptCATOpen(catalogPath, CRYPTCAT_OPEN_EXISTING, 0, CRYPTCAT_VERSION_2, X509_ASN_ENCODING_AND_PKCS_7);
        if (catalog is 0 or -1)
            return false;

        try
        {
            return CryptCATGetMemberInfo(catalog, sha256Hex.ToUpperInvariant()) != 0;
        }
        finally
        {
            CryptCATClose(catalog);
        }
    }
}
