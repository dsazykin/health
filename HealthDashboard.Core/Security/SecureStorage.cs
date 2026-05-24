using System;
using System.Runtime.InteropServices;

namespace HealthDashboard.Core.Security
{
    public class SecureStorage : ISecureStorage
    {
        private readonly ISecureStorage _impl;

        public SecureStorage()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _impl = new WindowsSecureStorage();
            }
            else
            {
                _impl = new MockSecureStorage();
            }
        }

        public string Encrypt(string plainText)
        {
            return _impl.Encrypt(plainText);
        }

        public string Decrypt(string cipherText)
        {
            return _impl.Decrypt(cipherText);
        }
    }
}
