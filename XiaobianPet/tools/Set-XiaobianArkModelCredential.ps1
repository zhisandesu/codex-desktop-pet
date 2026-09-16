[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$credentialTarget = 'XiaobianPet/ArkModelApi'

if (-not ('XiaobianPet.Tools.CredentialManager' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace XiaobianPet.Tools
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    public static class CredentialManager
    {
        [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CredWrite(ref NativeCredential credential, uint flags);
    }
}
'@
}

Write-Host 'Configure XiaobianPet standard Ark model API access'
Write-Host 'Use a standard Ark model API Key, not an Agent Plan TTS Key.'
Write-Host "Credential Manager target: $credentialTarget"
$apiKey = Read-Host 'Enter the standard Ark model API Key (input is hidden)' -AsSecureString

if ($apiKey.Length -eq 0) {
    $apiKey.Dispose()
    throw 'The API Key cannot be empty.'
}

$secretPointer = [IntPtr]::Zero
try {
    $secretPointer = [Runtime.InteropServices.Marshal]::SecureStringToCoTaskMemUnicode($apiKey)
    $credential = [XiaobianPet.Tools.NativeCredential]::new()
    $credential.Type = 1
    $credential.TargetName = $credentialTarget
    $credential.Comment = 'XiaobianPet standard Ark model API Key'
    $credential.CredentialBlobSize = [uint32]($apiKey.Length * 2)
    $credential.CredentialBlob = $secretPointer
    $credential.Persist = 2
    $credential.UserName = 'ArkModelApi'

    if (-not [XiaobianPet.Tools.CredentialManager]::CredWrite([ref]$credential, 0)) {
        $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw [ComponentModel.Win32Exception]::new($errorCode)
    }

    Write-Host 'Saved. Restart XiaobianPet if this run already received an authentication failure.' -ForegroundColor Green
}
finally {
    if ($secretPointer -ne [IntPtr]::Zero) {
        [Runtime.InteropServices.Marshal]::ZeroFreeCoTaskMemUnicode($secretPointer)
    }

    $apiKey.Dispose()
}
