using System;
using System.IO;

namespace FastLoader
{
    internal static class FastLz4Block
    {
        private const int MinMatch = 4;
        private const int MaxOffset = 65535;
        private const int HashBits = 16;
        private const int HashSize = 1 << HashBits;

        public static byte[] TryCompress(byte[] input)
        {
            if (input == null || input.Length < 16)
            {
                return null;
            }

            int inputLength = input.Length;
            byte[] output = new byte[inputLength + (inputLength / 255) + 16];
            int[] table = new int[HashSize];
            for (int i = 0; i < table.Length; i++)
            {
                table[i] = -1;
            }

            int anchor = 0;
            int inputIndex = 0;
            int outputIndex = 0;
            int matchLimit = inputLength - MinMatch;

            while (inputIndex <= matchLimit)
            {
                int hash = Hash(ReadInt(input, inputIndex));
                int referenceIndex = table[hash];
                table[hash] = inputIndex;

                if (referenceIndex >= 0 &&
                    inputIndex - referenceIndex <= MaxOffset &&
                    Equal4(input, referenceIndex, inputIndex))
                {
                    int literalLength = inputIndex - anchor;
                    int tokenIndex = outputIndex++;
                    int token;

                    if (literalLength >= 15)
                    {
                        token = 15 << 4;
                        outputIndex = WriteLength(output, outputIndex, literalLength - 15);
                        if (outputIndex < 0)
                        {
                            return null;
                        }
                    }
                    else
                    {
                        token = literalLength << 4;
                    }

                    if (outputIndex + literalLength + 2 > output.Length)
                    {
                        return null;
                    }

                    Buffer.BlockCopy(input, anchor, output, outputIndex, literalLength);
                    outputIndex += literalLength;

                    int offset = inputIndex - referenceIndex;
                    output[outputIndex++] = (byte)offset;
                    output[outputIndex++] = (byte)(offset >> 8);

                    int matchLength = MinMatch;
                    while (inputIndex + matchLength < inputLength &&
                           input[referenceIndex + matchLength] == input[inputIndex + matchLength])
                    {
                        matchLength++;
                    }

                    int encodedMatchLength = matchLength - MinMatch;
                    if (encodedMatchLength >= 15)
                    {
                        token |= 15;
                        outputIndex = WriteLength(output, outputIndex, encodedMatchLength - 15);
                        if (outputIndex < 0)
                        {
                            return null;
                        }
                    }
                    else
                    {
                        token |= encodedMatchLength;
                    }

                    output[tokenIndex] = (byte)token;
                    inputIndex += matchLength;
                    anchor = inputIndex;
                }
                else
                {
                    inputIndex++;
                }
            }

            int lastLiteralLength = inputLength - anchor;
            if (lastLiteralLength > 0 || outputIndex == 0)
            {
                int tokenIndex = outputIndex++;
                int token;
                if (lastLiteralLength >= 15)
                {
                    token = 15 << 4;
                    outputIndex = WriteLength(output, outputIndex, lastLiteralLength - 15);
                    if (outputIndex < 0)
                    {
                        return null;
                    }
                }
                else
                {
                    token = lastLiteralLength << 4;
                }

                if (outputIndex + lastLiteralLength > output.Length)
                {
                    return null;
                }

                output[tokenIndex] = (byte)token;
                Buffer.BlockCopy(input, anchor, output, outputIndex, lastLiteralLength);
                outputIndex += lastLiteralLength;
            }

            if (outputIndex >= inputLength)
            {
                return null;
            }

            byte[] compressed = new byte[outputIndex];
            Buffer.BlockCopy(output, 0, compressed, 0, outputIndex);
            return compressed;
        }

        public static byte[] Decompress(byte[] input, int outputLength)
        {
            if (input == null)
            {
                throw new InvalidDataException("LZ4 input is null.");
            }

            if (outputLength <= 0)
            {
                throw new InvalidDataException("Invalid LZ4 output length: " + outputLength);
            }

            byte[] output = new byte[outputLength];
            int inputIndex = 0;
            int outputIndex = 0;

            while (inputIndex < input.Length)
            {
                int token = input[inputIndex++];
                int literalLength = token >> 4;
                if (literalLength == 15)
                {
                    literalLength = ReadLength(input, ref inputIndex, literalLength);
                }

                if (literalLength < 0 ||
                    inputIndex + literalLength > input.Length ||
                    outputIndex + literalLength > output.Length)
                {
                    throw new InvalidDataException("Invalid LZ4 literal length.");
                }

                Buffer.BlockCopy(input, inputIndex, output, outputIndex, literalLength);
                inputIndex += literalLength;
                outputIndex += literalLength;

                if (inputIndex >= input.Length)
                {
                    break;
                }

                if (inputIndex + 2 > input.Length)
                {
                    throw new EndOfStreamException("LZ4 match offset was truncated.");
                }

                int offset = input[inputIndex] | (input[inputIndex + 1] << 8);
                inputIndex += 2;
                if (offset <= 0 || offset > outputIndex)
                {
                    throw new InvalidDataException("Invalid LZ4 match offset: " + offset);
                }

                int matchLength = token & 15;
                if (matchLength == 15)
                {
                    matchLength = ReadLength(input, ref inputIndex, matchLength);
                }

                matchLength += MinMatch;
                if (outputIndex + matchLength > output.Length)
                {
                    throw new InvalidDataException("Invalid LZ4 match length.");
                }

                CopyMatch(output, outputIndex - offset, ref outputIndex, matchLength);
            }

            if (outputIndex != output.Length)
            {
                throw new InvalidDataException("LZ4 output length mismatch: " + outputIndex + "/" + output.Length);
            }

            return output;
        }

        private static void CopyMatch(byte[] output, int referenceIndex, ref int outputIndex, int matchLength)
        {
            int offset = outputIndex - referenceIndex;
            if (offset >= matchLength)
            {
                Buffer.BlockCopy(output, referenceIndex, output, outputIndex, matchLength);
                outputIndex += matchLength;
                return;
            }

            int firstCopyLength = Math.Min(offset, matchLength);
            for (int i = 0; i < firstCopyLength; i++)
            {
                output[outputIndex + i] = output[referenceIndex + i];
            }

            outputIndex += firstCopyLength;
            matchLength -= firstCopyLength;
            int available = firstCopyLength;

            while (matchLength > 0)
            {
                int copyLength = Math.Min(available, matchLength);
                Buffer.BlockCopy(output, referenceIndex, output, outputIndex, copyLength);
                outputIndex += copyLength;
                matchLength -= copyLength;
                available += copyLength;
            }
        }

        private static int ReadLength(byte[] input, ref int inputIndex, int length)
        {
            while (true)
            {
                if (inputIndex >= input.Length)
                {
                    throw new EndOfStreamException("LZ4 length was truncated.");
                }

                int value = input[inputIndex++];
                length += value;
                if (value != 255)
                {
                    return length;
                }
            }
        }

        private static int WriteLength(byte[] output, int outputIndex, int length)
        {
            while (length >= 255)
            {
                if (outputIndex >= output.Length)
                {
                    return -1;
                }

                output[outputIndex++] = 255;
                length -= 255;
            }

            if (outputIndex >= output.Length)
            {
                return -1;
            }

            output[outputIndex++] = (byte)length;
            return outputIndex;
        }

        private static int Hash(int value)
        {
            unchecked
            {
                return (int)(((uint)value * 2654435761u) >> (32 - HashBits));
            }
        }

        private static int ReadInt(byte[] data, int index)
        {
            return data[index] |
                (data[index + 1] << 8) |
                (data[index + 2] << 16) |
                (data[index + 3] << 24);
        }

        private static bool Equal4(byte[] data, int left, int right)
        {
            return data[left] == data[right] &&
                data[left + 1] == data[right + 1] &&
                data[left + 2] == data[right + 2] &&
                data[left + 3] == data[right + 3];
        }
    }
}
