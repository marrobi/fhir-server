// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Health.Fhir.Core.Extensions;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Extensions
{
    public class Base64ExtensionsTests
    {
        [InlineData("{\"a\":\"<a>???\"}")] // this string, when base64 encoded, has / and + characters
        [InlineData("")]
        [InlineData("a")]
        [InlineData("aa")]
        [InlineData("aaa")]
        [InlineData("aaaa")]
        [InlineData("aaaaa")]
        [InlineData("aaaaaa")]
        [Theory]
        public static void GivenAString_WhenSafeBase64Encoded_ThenItCanBeDecoded(string input)
        {
            string safeBase64 = input.ToSafeBase64();
            Assert.Equal(safeBase64, Uri.EscapeDataString(safeBase64));
            Assert.Equal(input, safeBase64.FromSafeBase64ToString());
        }

        [InlineData(new byte[] { })]
        [InlineData(new byte[] { 0 })]
        [InlineData(new byte[] { 0, 0 })]
        [InlineData(new byte[] { 0, 0, 0 })]
        [InlineData(new byte[] { 0, 0, 0, 0 })]
        [Theory]
        public static void GivenAByteArray_WhenSafeBase64Encoded_ThenItCanBeDecoded(byte[] bytes)
        {
            string safeBase64 = new ReadOnlySpan<byte>(bytes).ToSafeBase64();
            Assert.Equal(safeBase64, Uri.EscapeDataString(safeBase64));
            Assert.Equal(bytes, safeBase64.FromSafeBase64ToBytes());
        }
    }
}
