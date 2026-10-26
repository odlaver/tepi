using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace Tepi
{
    // Authenticode check: signed by a trusted chain, and who signed it
    static class Signature
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WinTrustFile
        {
            public int cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile, pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TrustData
        {
            public int cbStruct;
            public IntPtr pPolicyCallbackData, pSIPClientData;
            public int dwUIChoice, fdwRevocationChecks, dwUnionChoice;
            public IntPtr pFile;
            public int dwStateAction;
            public IntPtr hWVTStateData, pwszURLReference;
            public int dwProvFlags, dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref TrustData data);

        static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        // Publisher subject when validly signed, else null
        public static string Signer(string path)
        {
            if (!Trusted(path)) return null;
            try { return new X509Certificate(X509Certificate.CreateFromSignedFile(path)).Subject; }
            catch (Exception) { return null; }
        }

        static bool Trusted(string path)
        {
            var file = new WinTrustFile { cbStruct = Marshal.SizeOf(typeof(WinTrustFile)), pcwszFilePath = path };
            IntPtr pFile = Marshal.AllocHGlobal(file.cbStruct);
            try
            {
                Marshal.StructureToPtr(file, pFile, false);
                // No UI, no online revocation lookup, a file
                var data = new TrustData { cbStruct = Marshal.SizeOf(typeof(TrustData)), dwUIChoice = 2, fdwRevocationChecks = 0, dwUnionChoice = 1, pFile = pFile, dwStateAction = 1 };
                int result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
                data.dwStateAction = 2;
                WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
                return result == 0;
            }
            finally
            {
                Marshal.DestroyStructure(pFile, typeof(WinTrustFile));
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
