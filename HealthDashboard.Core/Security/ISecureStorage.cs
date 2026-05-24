namespace HealthDashboard.Core.Security
{
    public interface ISecureStorage
    {
        string Encrypt(string plainText);
        string Decrypt(string cipherText);
    }
}
