using System;
using System.Text;

namespace HealthDashboard.Core.Security
{
    public class MockSecureStorage : ISecureStorage
    {
        private const string MockPrefix = "mock_enc:";

        public string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return string.Empty;
            byte[] bytes = Encoding.UTF8.GetBytes(plainText);
            return MockPrefix + Convert.ToBase64String(bytes);
        }

        public string Decrypt(string cipherText)
        {
            if (string.IsNullOrEmpty(cipherText)) return string.Empty;
            if (cipherText.StartsWith(MockPrefix))
            {
                cipherText = cipherText.Substring(MockPrefix.Length);
            }
            byte[] bytes = Convert.FromBase64String(cipherText);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
