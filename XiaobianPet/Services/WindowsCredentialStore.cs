using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace XiaobianPet.Services;

internal static class WindowsCredentialStore
{
    internal const string ArkAgentPlanTarget = "XiaobianPet/ArkAgentPlanTts";
    internal const string ArkModelApiTarget = "XiaobianPet/ArkModelApi";

    private const uint GenericCredentialType = 1;

    internal static string? GetArkAgentPlanApiKey()
    {
        var apiKey = Environment.GetEnvironmentVariable(
            "XIAOBIAN_ARK_API_KEY",
            EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey.Trim();
        }

        apiKey = ReadGenericCredential(ArkAgentPlanTarget);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey.Trim();
        }

        return null;
    }

    internal static string? GetArkModelApiKey()
    {
        var apiKey = Environment.GetEnvironmentVariable(
            "XIAOBIAN_ARK_MODEL_API_KEY",
            EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey.Trim();
        }

        // The model API has its own target by design. Never fall back to
        // ArkAgentPlanTarget: Agent Plan keys are not valid model API credentials.
        apiKey = ReadGenericCredential(ArkModelApiTarget);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            return apiKey.Trim();
        }

        return null;
    }

    private static string? ReadGenericCredential(string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!CredRead(target, GenericCredentialType, 0, out var credentialPointer))
        {
            // A missing or inaccessible credential simply leaves the cloud backend
            // unavailable. SpeechService will use the local SAPI fallback.
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                var secret = Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
                return string.IsNullOrWhiteSpace(secret) ? null : secret;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out IntPtr credentialPointer);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
