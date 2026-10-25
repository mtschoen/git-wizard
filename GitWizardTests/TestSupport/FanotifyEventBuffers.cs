using System.Text;
using GitWizard.Watch.Linux;

namespace GitWizardTests.Watch.Linux;

/// <summary>Builds synthetic fanotify event buffers and kernel file-handle payloads for decoder tests.</summary>
static class FanotifyEventBuffers
{
    public static byte[] GetFullHandleBytes(byte[] fHandle)
    {
        var payload = new byte[8 + fHandle.Length];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), (uint)fHandle.Length);
        BitConverter.TryWriteBytes(payload.AsSpan(4, 4), 1); // type 1
        Buffer.BlockCopy(fHandle, 0, payload, 8, fHandle.Length);
        return payload;
    }

    public static byte[] CreateSyntheticEventBuffer(
        ulong mask,
        byte[] fHandle,
        string fileName,
        byte infoType = FanotifyConstants.EventInfoTypeDfidName,
        long filesystemId = 0)
    {
        var nameBytes = Encoding.UTF8.GetBytes(fileName);
        var infoLen = 20 + fHandle.Length + nameBytes.Length + 1;
        var eventLen = 24 + infoLen;

        var buffer = new byte[eventLen];

        // Metadata header (24 bytes)
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), (uint)eventLen);
        buffer[4] = 3; // version 3
        buffer[5] = 0; // reserved
        BitConverter.TryWriteBytes(buffer.AsSpan(6, 2), (ushort)24); // metadata_len
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 8), mask);
        BitConverter.TryWriteBytes(buffer.AsSpan(16, 4), -1); // fd = FAN_NOFD
        BitConverter.TryWriteBytes(buffer.AsSpan(20, 4), 1234); // pid

        // Info record header (4 bytes)
        buffer[24] = infoType;
        buffer[25] = 0;
        BitConverter.TryWriteBytes(buffer.AsSpan(26, 2), (ushort)infoLen);

        // fsid (8 bytes)
        BitConverter.TryWriteBytes(buffer.AsSpan(28, 8), filesystemId);

        // file_handle (8 + fHandle.Length bytes)
        BitConverter.TryWriteBytes(buffer.AsSpan(36, 4), (uint)fHandle.Length);
        BitConverter.TryWriteBytes(buffer.AsSpan(40, 4), 1); // handle_type
        Buffer.BlockCopy(fHandle, 0, buffer, 44, fHandle.Length);

        // fileName + null terminator
        var nameStart = 44 + fHandle.Length;
        Buffer.BlockCopy(nameBytes, 0, buffer, nameStart, nameBytes.Length);
        buffer[nameStart + nameBytes.Length] = 0;

        return buffer;
    }

    public static byte[] CreateSyntheticOverflowBuffer()
    {
        var buffer = new byte[24];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), (uint)24);
        buffer[4] = 3; // version 3
        buffer[5] = 0; // reserved
        BitConverter.TryWriteBytes(buffer.AsSpan(6, 2), (ushort)24); // metadata_len
        BitConverter.TryWriteBytes(buffer.AsSpan(8, 8), FanotifyConstants.QueueOverflow);
        BitConverter.TryWriteBytes(buffer.AsSpan(16, 4), -1); // fd = FAN_NOFD
        BitConverter.TryWriteBytes(buffer.AsSpan(20, 4), 0); // pid
        return buffer;
    }

    public static byte[] CombineBuffers(params byte[][] buffers)
    {
        var totalLength = buffers.Sum(b => b.Length);
        var result = new byte[totalLength];
        var offset = 0;
        foreach (var buffer in buffers)
        {
            Buffer.BlockCopy(buffer, 0, result, offset, buffer.Length);
            offset += buffer.Length;
        }
        return result;
    }
}
