namespace NLightning.Infrastructure.Converters;

/// <summary>
/// Fixed-width big/little-endian conversions.
/// </summary>
/// <remarks>
/// The old "trim to minimum length" and "pad with zero" helpers were removed (NL-015): the LE variants did not
/// implement BOLT's truncated-integer semantics (BOLT 1/2/3 trim integers from the high-order end in big-endian
/// layout), and no caller was left. Minimal encodings go through <c>BinaryPrimitives</c> or the shared
/// <c>TruncatedInt</c> helper (<c>NLightning.Infrastructure.Converters.TruncatedInt</c>).
/// </remarks>
public static class EndianBitConverter
{
    #region GetBytesBE
    /// <summary>
    /// Converts a ulong to a byte array in big-endian order.
    /// </summary>
    /// <param name="value">The ulong to convert.</param>
    /// <returns>The byte array representation of the ulong.</returns>
    public static byte[] GetBytesBigEndian(ulong value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    /// <summary>
    /// Converts a long to a byte array in big-endian order.
    /// </summary>
    /// <param name="value">The long to convert.</param>
    /// <returns>The byte array representation of the long.</returns>
    public static byte[] GetBytesBigEndian(long value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    /// <summary>
    /// Converts a uint to a byte array in big-endian order.
    /// </summary>
    /// <param name="value">The uint to convert.</param>
    /// <returns>The byte array representation of the uint.</returns>
    public static byte[] GetBytesBigEndian(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    /// <summary>
    /// Converts a int to a byte array in big-endian order.
    /// </summary>
    /// <param name="value">The int to convert.</param>
    /// <returns>The byte array representation of the int.</returns>
    public static byte[] GetBytesBigEndian(int value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    /// <summary>
    /// Converts a ushort to a byte array in big-endian order.
    /// </summary>
    /// <param name="value">The ushort to convert.</param>
    /// <returns>The byte array representation of the ushort.</returns>
    public static byte[] GetBytesBigEndian(ushort value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    /// <summary>
    /// Converts a short to a byte array in big-endian order.
    /// </summary>
    /// <param name="value">The short to convert.</param>
    /// <returns>The byte array representation of the short.</returns>
    public static byte[] GetBytesBigEndian(short value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }
    #endregion

    #region GetBytesLE
    /// <summary>
    /// Converts a ulong to a byte array in little-endian order.
    /// </summary>
    /// <param name="value">The ulong to convert.</param>
    /// <returns>The byte array representation of the ulong.</returns>
    public static byte[] GetBytesLittleEndian(ulong value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    /// <summary>
    /// Converts a uint to a byte array in little-endian order.
    /// </summary>
    /// <param name="value">The uint to convert.</param>
    /// <returns>The byte array representation of the uint.</returns>
    public static byte[] GetBytesLittleEndian(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }

    /// <summary>
    /// Converts a ushort to a byte array in little-endian order.
    /// </summary>
    /// <param name="value">The ushort to convert.</param>
    /// <returns>The byte array representation of the ushort.</returns>
    public static byte[] GetBytesLittleEndian(ushort value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return bytes;
    }
    #endregion

    #region Back From LE Bytes
    /// <summary>
    /// Converts a byte array to an ulong in little-endian order.
    /// </summary>
    /// <param name="bytes">The bytes to convert; must be exactly 8.</param>
    /// <returns>The ulong representation of the byte array.</returns>
    public static ulong ToUInt64LittleEndian(ReadOnlySpan<byte> bytes)
    {
        var paddedBytes = bytes.ToArray();

        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(paddedBytes);
        }
        return BitConverter.ToUInt64(paddedBytes, 0);
    }

    /// <summary>
    /// Converts a byte array to a long in little-endian order.
    /// </summary>
    /// <param name="bytes">The bytes to convert; must be exactly 8.</param>
    /// <returns>The long representation of the byte array.</returns>
    public static long ToInt64LittleEndian(ReadOnlySpan<byte> bytes)
    {
        var paddedBytes = bytes.ToArray();

        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(paddedBytes);
        }
        return BitConverter.ToInt64(paddedBytes, 0);
    }

    /// <summary>
    /// Converts a byte array to a uint in little-endian order.
    /// </summary>
    /// <param name="bytes">The bytes to convert; must be exactly 4.</param>
    /// <returns>The uint representation of the byte array.</returns>
    public static uint ToUInt32LittleEndian(ReadOnlySpan<byte> bytes)
    {
        var paddedBytes = bytes.ToArray();

        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(paddedBytes);
        }
        return BitConverter.ToUInt32(paddedBytes, 0);
    }

    /// <summary>
    /// Converts a byte array to a int in little-endian order.
    /// </summary>
    /// <param name="bytes">The bytes to convert; must be exactly 4.</param>
    /// <returns>The int representation of the byte array.</returns>
    public static int ToInt32LittleEndian(ReadOnlySpan<byte> bytes)
    {
        var paddedBytes = bytes.ToArray();

        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(paddedBytes);
        }
        return BitConverter.ToInt32(paddedBytes, 0);
    }

    /// <summary>
    /// Converts a byte array to a ushort in little-endian order.
    /// </summary>
    /// <param name="bytes">The bytes to convert; must be exactly 2.</param>
    /// <returns>The ushort representation of the byte array.</returns>
    public static ushort ToUInt16LittleEndian(ReadOnlySpan<byte> bytes)
    {
        var paddedBytes = bytes.ToArray();

        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(paddedBytes);
        }
        return BitConverter.ToUInt16(paddedBytes, 0);
    }

    /// <summary>
    /// Converts a byte array to a short in little-endian order.
    /// </summary>
    /// <param name="bytes">The bytes to convert; must be exactly 2.</param>
    /// <returns>The short representation of the byte array.</returns>
    public static short ToInt16LittleEndian(ReadOnlySpan<byte> bytes)
    {
        var paddedBytes = bytes.ToArray();

        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(paddedBytes);
        }
        return BitConverter.ToInt16(paddedBytes, 0);
    }
    #endregion

    #region Back From BE Bytes
    /// <summary>
    /// Converts a byte array to a ulong in big-endian order.
    /// </summary>
    /// <param name="bytes">The bytes to convert; must be exactly 8.</param>
    /// <returns>The ulong representation of the byte array.</returns>
    public static ulong ToUInt64BigEndian(ReadOnlySpan<byte> bytes)
    {
        var paddedBytes = bytes.ToArray();

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(paddedBytes);
        }
        return BitConverter.ToUInt64(paddedBytes, 0);
    }

    /// <summary>
    /// Converts a byte array to a long in big-endian order.
    /// </summary>
    /// <param name="bytes">The bytes to convert; must be exactly 8.</param>
    /// <returns>The long representation of the byte array.</returns>
    public static long ToInt64BigEndian(byte[] bytes)
    {
        var paddedBytes = bytes.ToArray();

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(paddedBytes);
        }
        return BitConverter.ToInt64(paddedBytes, 0);
    }

    /// <summary>
    /// Converts a byte array to a uint in big-endian order.
    /// </summary>
    /// <param name="bytes">The bytes to convert; must be exactly 4.</param>
    /// <returns>The uint representation of the byte array.</returns>
    public static uint ToUInt32BigEndian(ReadOnlySpan<byte> bytes)
    {
        var paddedBytes = bytes.ToArray();

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(paddedBytes);
        }
        return BitConverter.ToUInt32(paddedBytes, 0);
    }

    /// <summary>
    /// Converts a byte array to a int in big-endian order.
    /// </summary>
    /// <param name="bytes">The bytes to convert; must be exactly 4.</param>
    /// <returns>The int representation of the byte array.</returns>
    public static int ToInt32BigEndian(ReadOnlySpan<byte> bytes)
    {
        var paddedBytes = bytes.ToArray();

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(paddedBytes);
        }
        return BitConverter.ToInt32(paddedBytes, 0);
    }

    /// <summary>
    /// Converts a byte array to a ushort in big-endian order.
    /// </summary>
    /// <param name="bytes">The bytes to convert; must be exactly 2.</param>
    /// <returns>The ushort representation of the byte array.</returns>
    public static ushort ToUInt16BigEndian(ReadOnlySpan<byte> bytes)
    {
        var paddedBytes = bytes.ToArray();

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(paddedBytes);
        }
        return BitConverter.ToUInt16(paddedBytes, 0);
    }

    /// <summary>
    /// Converts a byte array to a short in big-endian order.
    /// </summary>
    /// <param name="bytes">The bytes to convert; must be exactly 2.</param>
    /// <returns>The short representation of the byte array.</returns>
    public static short ToInt16BigEndian(ReadOnlySpan<byte> bytes)
    {
        var paddedBytes = bytes.ToArray();

        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(paddedBytes);
        }
        return BitConverter.ToInt16(paddedBytes, 0);
    }
    #endregion
}