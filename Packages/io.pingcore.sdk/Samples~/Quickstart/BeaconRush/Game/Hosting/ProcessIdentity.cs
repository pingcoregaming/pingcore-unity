using System.Security.Cryptography;
using System.Text;

namespace BeaconRush.Hosting
{
    /// <summary>A fresh random id per game process, so a recycle is visible as a new <c>processId</c>.</summary>
    public static class ProcessIdentity
    {
        /// <summary>128 random bits as 32 lowercase hex characters.</summary>
        public static string NewProcessId()
        {
            var bytes = new byte[16];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }

            var hex = new StringBuilder(32);
            foreach (byte b in bytes)
            {
                hex.Append(b.ToString("x2"));
            }

            return hex.ToString();
        }
    }
}
