using System;
using System.Runtime.InteropServices;
using Xunit;
using HealthDashboard.Core.Security;

namespace HealthDashboard.Tests
{
    public class SecureStorageTests
    {
        [Fact]
        public void TestMockSecureStorage_EncryptDecrypt()
        {
            var storage = new MockSecureStorage();
            var original = "super-secret-token-123456";

            var encrypted = storage.Encrypt(original);
            Assert.NotEqual(original, encrypted);
            Assert.StartsWith("mock_enc:", encrypted);

            var decrypted = storage.Decrypt(encrypted);
            Assert.Equal(original, decrypted);
        }

        [Fact]
        public void TestMockSecureStorage_EmptyInputs()
        {
            var storage = new MockSecureStorage();
            Assert.Equal(string.Empty, storage.Encrypt(string.Empty));
            Assert.Equal(string.Empty, storage.Decrypt(string.Empty));
        }

        [Fact]
        public void TestWindowsSecureStorage_CrossPlatformSafety()
        {
            var storage = new WindowsSecureStorage();
            var original = "test-secret-value";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var encrypted = storage.Encrypt(original);
                Assert.NotEqual(original, encrypted);

                var decrypted = storage.Decrypt(encrypted);
                Assert.Equal(original, decrypted);
            }
            else
            {
                Assert.Throws<PlatformNotSupportedException>(() => storage.Encrypt(original));
                Assert.Throws<PlatformNotSupportedException>(() => storage.Decrypt("some-base64"));
            }
        }

        [Fact]
        public void TestSecureStorageResolver_WorksOnAllPlatforms()
        {
            var storage = new SecureStorage();
            var original = "unified-secret-test";

            // Should successfully encrypt and decrypt on whatever host OS is running
            var encrypted = storage.Encrypt(original);
            Assert.NotEqual(original, encrypted);

            var decrypted = storage.Decrypt(encrypted);
            Assert.Equal(original, decrypted);
        }
    }
}
