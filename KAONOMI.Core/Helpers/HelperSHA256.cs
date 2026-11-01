namespace KAONOMI.Core.Helpers
{
    using System;
    using System.IO;
    using System.Security.Cryptography;
    using System.Text;

    /// <summary>
    /// SHA-256 hashing utilities for strings and files.
    /// </summary>
    public class HelperSHA256
    {
        /// <summary>Computes the SHA-256 hash of a file and returns it as Base64.</summary>
        public static string FromFileGet(string filePath)
        {
            using (SHA256 SHA256 = SHA256Managed.Create())
            {
                using (FileStream fileStream = File.OpenRead(filePath))
                    return Convert.ToBase64String(SHA256.ComputeHash(fileStream));
            }
        }
        /// <summary>Computes the SHA-256 hash of a UTF-8 string and returns it as lowercase hexadecimal.</summary>
        public static string Get(string text)
        {
            try
            {
                SHA256 hash = SHA256.Create();
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(text));
                StringBuilder sha256signature = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    sha256signature.Append(bytes[i].ToString("x2"));
                }
                return sha256signature.ToString();
            }
            catch (Exception)
            {
                throw;
            }
        }
    }
}
