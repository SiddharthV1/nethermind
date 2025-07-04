// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

public class ByteArrayConverter : JsonConverter<byte[]>
{
    private static readonly ushort _hexPrefix = MemoryMarshal.Cast<byte, ushort>("0x"u8)[0];

    public override byte[]? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return Convert(ref reader);
    }

    public static byte[]? Convert(ref Utf8JsonReader reader)
    {
        JsonTokenType tokenType = reader.TokenType;
        if (tokenType == JsonTokenType.None || tokenType == JsonTokenType.Null)
            return null;
        else if (tokenType != JsonTokenType.String && tokenType != JsonTokenType.PropertyName)
        {
            ThrowInvalidOperationException();
        }

        var length = reader.ValueSpan.Length;
        byte[]? bytes = null;
        if (length == 0)
        {
            length = checked((int)reader.ValueSequence.Length);
            if (length == 0)
                return null;

            bytes = ArrayPool<byte>.Shared.Rent(length);
            reader.ValueSequence.CopyTo(bytes);
        }

        ReadOnlySpan<byte> hex = bytes is null ? reader.ValueSpan : bytes.AsSpan(0, length);
        if (length >= 2 && Unsafe.As<byte, ushort>(ref MemoryMarshal.GetReference(hex)) == _hexPrefix)
            hex = hex[2..];

        var returnVal = Bytes.FromUtf8HexString(hex);
        if (bytes is not null)
            ArrayPool<byte>.Shared.Return(bytes);

        return returnVal;
    }

    public static void Convert(ref Utf8JsonReader reader, scoped Span<byte> span)
    {
        JsonTokenType tokenType = reader.TokenType;
        if (tokenType == JsonTokenType.None || tokenType == JsonTokenType.Null)
        {
            return;
        }

        if (tokenType != JsonTokenType.String)
        {
            ThrowInvalidOperationException();
        }

        ReadOnlySpan<byte> hex = reader.ValueSpan;
        if (hex.Length >= 2 && Unsafe.As<byte, ushort>(ref MemoryMarshal.GetReference(hex)) == _hexPrefix)
        {
            hex = hex[2..];
        }

        Bytes.FromUtf8HexString(hex, span);
    }

    [DoesNotReturn, StackTraceHidden]
    internal static void ThrowInvalidOperationException()
    {
        throw new InvalidOperationException();
    }

    public override void Write(
        Utf8JsonWriter writer,
        byte[] bytes,
        JsonSerializerOptions options)
    {
        Convert(writer, bytes, skipLeadingZeros: false);
    }

    [SkipLocalsInit]
    public static void Convert(Utf8JsonWriter writer, ReadOnlySpan<byte> bytes, bool skipLeadingZeros = true, bool addHexPrefix = true)
    {
        Convert(writer,
            bytes,
            static (w, h) => w.WriteRawValue(h, skipInputValidation: true), skipLeadingZeros, addHexPrefix: addHexPrefix);
    }

    public delegate void WriteHex(Utf8JsonWriter writer, ReadOnlySpan<byte> hex);

    [SkipLocalsInit]
    public static void Convert(
        Utf8JsonWriter writer,
        ReadOnlySpan<byte> bytes,
        WriteHex writeAction,
        bool skipLeadingZeros = true,
        bool addQuotations = true,
        bool addHexPrefix = true)
    {
        const int maxStackLength = 128;
        const int stackLength = 256;

        var leadingNibbleZeros = skipLeadingZeros ? bytes.CountLeadingNibbleZeros() : 0;
        var nibblesCount = bytes.Length * 2;

        if (skipLeadingZeros && nibblesCount is not 0 && leadingNibbleZeros == nibblesCount)
        {
            writer.WriteStringValue(Bytes.ZeroHexValue);
            return;
        }

        var prefixLength = addHexPrefix ? 2 : 0;
        var length = nibblesCount - leadingNibbleZeros + prefixLength + (addQuotations ? 2 : 0);

        byte[]? array = null;
        if (length > maxStackLength)
            array = ArrayPool<byte>.Shared.Rent(length);

        Span<byte> hex = (array ?? stackalloc byte[stackLength])[..length];
        var start = 0;
        Index end = ^0;
        if (addQuotations)
        {
            end = ^1;
            hex[^1] = (byte)'"';
            hex[start++] = (byte)'"';
        }

        if (addHexPrefix)
        {
            hex[start++] = (byte)'0';
            hex[start++] = (byte)'x';
        }

        Span<byte> output = hex[start..end];

        ReadOnlySpan<byte> input = bytes[(leadingNibbleZeros / 2)..];
        input.OutputBytesToByteHex(output, extraNibble: (leadingNibbleZeros & 1) != 0);
        writeAction(writer, hex);

        if (array is not null)
            ArrayPool<byte>.Shared.Return(array);
    }
}
