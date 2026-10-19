using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FudanNetLite
{
    // Recovered from the selected SRun3K client. These methods perform no I/O.
    // The installed client's credentials are ASCII. Its ANSI code page behavior
    // for non-ASCII credentials has not been established by runtime evidence.
    public static class LegacySrun
    {
        public const int PasswordKey = 1234567890;
        private static readonly Encoding Ascii = Encoding.GetEncoding(20127,
            EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);

        // FUN_004187d8 / FUN_00419412, PortalAuthPasswordMode=1.
        // Return the raw field value: the transport must form-encode it once.
        public static string EncodePassword(string password, int n)
        {
            if (password == null) throw new ArgumentNullException("password");
            if (n < 0) throw new ArgumentOutOfRangeException("n");
            byte[] input = Ascii.GetBytes(password);
            string key = n.ToString(CultureInfo.InvariantCulture);
            int keyIndex = key.Length - 1;
            char[] output = new char[input.Length * 2];
            for (int i = 0; i < input.Length; ++i)
            {
                int value = input[i] ^ (byte)key[keyIndex];
                char high = (char)((value >> 4) + 99);
                char low = (char)((value & 15) + 54);
                output[i * 2] = (keyIndex & 1) == 0 ? high : low;
                output[i * 2 + 1] = (keyIndex & 1) == 0 ? low : high;
                if (--keyIndex < 0) keyIndex = key.Length - 1;
            }
            Array.Clear(input, 0, input.Length);
            return new string(output);
        }

        // FUN_0040db2d. Case matters when the result is hashed in a header.
        public static string UrlEncode(string value)
        {
            if (value == null) throw new ArgumentNullException("value");
            byte[] input = Ascii.GetBytes(value);
            StringBuilder output = new StringBuilder(input.Length * 3);
            foreach (byte b in input)
            {
                if ((b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') ||
                    (b >= '0' && b <= '9') || b == '.' || b == '-' || b == '_')
                    output.Append((char)b);
                else if (b == ' ') output.Append('+');
                else output.Append('%').Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            Array.Clear(input, 0, input.Length);
            return output.ToString();
        }

        // FUN_0041b937 -> FUN_0040108e (HMAC-SHA1) ->
        // FUN_00401e92 (MD5 lowercase hex of the 20 raw HMAC bytes).
        public static string AuthorizationKey(string password, string mac)
        {
            byte[] key = Ascii.GetBytes(UrlEncode(password));
            byte[] data = Ascii.GetBytes(UrlEncode(mac));
            byte[] hmac = null;
            try
            {
                using (HMACSHA1 hash = new HMACSHA1(key)) hmac = hash.ComputeHash(data);
                using (MD5 hash = MD5.Create()) return Hex(hash.ComputeHash(hmac));
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
                Array.Clear(data, 0, data.Length);
                if (hmac != null) Array.Clear(hmac, 0, hmac.Length);
            }
        }

        // FUN_00401000 / 00401046 / 0040107e / 00419e7c.
        // CRC-32/ISO-HDLC reflected polynomial, with no final XOR in this client.
        public static string Crc32State(string input)
        {
            if (input == null) throw new ArgumentNullException("input");
            byte[] bytes = Ascii.GetBytes(input);
            uint state = 0xffffffffu;
            foreach (byte value in bytes)
            {
                state ^= value;
                for (int i = 0; i < 8; ++i)
                    state = (state >> 1) ^ ((state & 1) != 0 ? 0xedb88320u : 0u);
            }
            Array.Clear(bytes, 0, bytes.Length);
            return state.ToString("X8", CultureInfo.InvariantCulture);
        }

        // FUN_00437d5b: plain Portal heartbeat, destination UDP 3338.
        // Initial userId is CHECK_VERSION; later use the confirmed session user.
        // msgId represents the original object's +0xac field; its meaning should
        // be determined from the server response before treating it as a counter.
        public static byte[] BuildPortalHeartbeat(string userId, string mac, uint msgId)
        {
            if (userId == null) throw new ArgumentNullException("userId");
            if (mac == null) throw new ArgumentNullException("mac");
            byte[] output = new byte[60];
            CopyAscii(userId, output, 0, 32);
            CopyAscii(mac, output, 32, 20);
            output[52] = (byte)msgId;
            output[53] = (byte)(msgId >> 8);
            output[54] = (byte)(msgId >> 16);
            output[55] = (byte)(msgId >> 24);
            return output;
        }

        private static void CopyAscii(string value, byte[] output, int offset, int size)
        {
            byte[] bytes = Ascii.GetBytes(value);
            if (bytes.Length >= size) throw new ArgumentException("Field exceeds protocol size.");
            Buffer.BlockCopy(bytes, 0, output, offset, bytes.Length);
        }

        private static string Hex(byte[] bytes)
        {
            StringBuilder result = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes) result.Append(value.ToString("x2", CultureInfo.InvariantCulture));
            return result.ToString();
        }
    }
}
