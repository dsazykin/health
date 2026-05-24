using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HealthDashboard.Core.Security
{
    public class WindowsSecureStorage : ISecureStorage
    {
        public string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("WindowsSecureStorage is only supported on Windows.");
            }

            byte[] data = Encoding.UTF8.GetBytes(plainText);
            byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        public string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("WindowsSecureStorage is only supported on Windows.");
            }

            byte[] data = Convert.FromBase64String(cipherText);
            byte[] decrypted = ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
    }
}
