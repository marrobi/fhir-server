// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Buffers;
using System.Text;

namespace Microsoft.Health.Fhir.Core.Extensions
{
    public static class Base64Extensions
    {
        /// <summary>
        /// Encodes a string to UTF8 and then encoded as a base64
        /// </summary>
        /// <param name="input">The string to encode</param>
        /// <returns>An encoded string that's safe to be used in URLs.</returns>
        public static string ToSafeBase64(this string input)
        {
            var bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(input.Length));
            try
            {
                int byteLength = Encoding.UTF8.GetBytes(input.AsSpan(), bytes.AsSpan());
                return ToSafeBase64(bytes.AsSpan(0, byteLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        /// <summary>
        /// Encodes a byte span to Base64 safely, this can be used in URLs.
        /// </summary>
        /// <param name="bytes">The bytes to encode.</param>
        /// <returns>An encoded string that's safe to be used in URLs.</returns>
        public static string ToSafeBase64(this ReadOnlySpan<byte> bytes)
        {
            var chars = ArrayPool<char>.Shared.Rent(((4 * bytes.Length / 3) + 3) & ~3);
            try
            {
                if (!Convert.TryToBase64Chars(bytes, chars.AsSpan(), out var stringLength))
                {
                    throw new InvalidOperationException($"Failure calling {nameof(Convert.TryToBase64Chars)}");
                }

                // remove padding at end
                while (stringLength > 0 && chars[stringLength - 1] == '=')
                {
                    stringLength--;
                }

                Span<char> span = chars.AsSpan(0, stringLength);
                ToUriSafeChars(span);

                var s = new string(span);
                return s;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(chars);
            }
        }

        /// <summary>
        /// Converts a string that was encoded with <see cref="ToSafeBase64(string)"/> to its original string.
        /// </summary>
        /// <param name="input">The safe base64-encoded string</param>
        /// <returns>The decoded string</returns>
        public static string FromSafeBase64ToString(this string input)
        {
            char[] chars = ArrayPool<char>.Shared.Rent(input.Length + 2);
            byte[] bytes = null;

            try
            {
                input.CopyTo(0, chars, 0, input.Length);
                int charsLength = input.Length;

                switch (charsLength % 4)
                {
                    case 2:
                        chars[charsLength++] = '=';
                        chars[charsLength++] = '=';
                        break;
                    case 3:
                        chars[charsLength++] = '=';
                        break;
                }

                Span<char> charSpan = chars.AsSpan(0, charsLength);

                for (int i = 0; i < charSpan.Length; i++)
                {
                    switch (charSpan[i])
                    {
                        case '-':
                            charSpan[i] = '+';
                            break;
                        case '_':
                            charSpan[i] = '/';
                            break;
                    }
                }

                bytes = ArrayPool<byte>.Shared.Rent(charsLength * 3 / 4);

                if (!Convert.TryFromBase64Chars(charSpan, bytes.AsSpan(), out int bytesWritten))
                {
                    throw new InvalidOperationException($"Failure in calling {nameof(Convert.TryFromBase64Chars)}");
                }

                return Encoding.UTF8.GetString(bytes.AsSpan(0, bytesWritten));
            }
            finally
            {
                ArrayPool<char>.Shared.Return(chars);
                if (bytes != null)
                {
                    ArrayPool<byte>.Shared.Return(bytes);
                }
            }
        }

        /// <summary>
        /// Converts a byte array that was encoded with <see cref="ToSafeBase64(string)"/> to its original byte array.
        /// </summary>
        /// <param name="input">The safe base-64 encoded string</param>
        /// <returns>The decode array</returns>
        public static byte[] FromSafeBase64ToBytes(this string input)
        {
            char[] chars = ArrayPool<char>.Shared.Rent(input.Length + 2);

            try
            {
                input.CopyTo(0, chars, 0, input.Length);

                int charsLength = input.Length;

                switch (input.Length % 4)
                {
                    case 2:
                        chars[charsLength++] = '=';
                        chars[charsLength++] = '=';
                        break;
                    case 3:
                        chars[charsLength++] = '=';
                        break;
                }

                FromUriSafeChars(chars.AsSpan(0, charsLength));

                return Convert.FromBase64CharArray(chars, 0, charsLength);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(chars);
            }
        }

        private static void ToUriSafeChars(Span<char> chars)
        {
            for (int i = 0; i < chars.Length; i++)
            {
                switch (chars[i])
                {
                    case '+':
                        chars[i] = '-';
                        break;
                    case '/':
                        chars[i] = '_';
                        break;
                }
            }
        }

        private static void FromUriSafeChars(Span<char> chars)
        {
            for (int i = 0; i < chars.Length; i++)
            {
                switch (chars[i])
                {
                    case '-':
                        chars[i] = '+';
                        break;
                    case '_':
                        chars[i] = '/';
                        break;
                }
            }
        }
    }
}
