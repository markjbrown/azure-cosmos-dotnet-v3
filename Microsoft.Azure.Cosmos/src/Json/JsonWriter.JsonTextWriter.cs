﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------
namespace Microsoft.Azure.Cosmos.Json
{
    using System;
    using System.Buffers;
    using System.Buffers.Text;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Text;

    /// <summary>
    /// Partial class for the JsonWriter that has a private JsonTextWriter below.
    /// </summary>
#if INTERNAL
    public
#else
    internal
#endif
    abstract partial class JsonWriter : IJsonWriter
    {
        /// <summary>
        /// This class is used to build a JSON string.
        /// It supports our defined IJsonWriter interface.
        /// It keeps an stack to keep track of scope, and provides error checking using that.
        /// It has few other variables for error checking
        /// The user can also provide initial size to reserve string buffer, that will help reduce cost of reallocation.
        /// It provides error checking based on JSON grammar. It provides escaping for nine characters specified in JSON.
        /// </summary>
        private sealed class JsonTextWriter : JsonWriter
        {
            private const byte ValueSeperatorToken = (byte)':';
            private const byte MemberSeperatorToken = (byte)',';
            private const byte ObjectStartToken = (byte)'{';
            private const byte ObjectEndToken = (byte)'}';
            private const byte ArrayStartToken = (byte)'[';
            private const byte ArrayEndToken = (byte)']';
            private const byte PropertyStartToken = (byte)'"';
            private const byte PropertyEndToken = (byte)'"';
            private const byte StringStartToken = (byte)'"';
            private const byte StringEndToken = (byte)'"';

            private const byte Int8TokenPrefix = (byte)'I';
            private const byte Int16TokenPrefix = (byte)'H';
            private const byte Int32TokenPrefix = (byte)'L';
            private const byte UnsignedTokenPrefix = (byte)'U';
            private const byte FloatTokenPrefix = (byte)'S';
            private const byte DoubleTokenPrefix = (byte)'D';
            private const byte GuidTokenPrefix = (byte)'G';
            private const byte BinaryTokenPrefix = (byte)'B';

            private static readonly ReadOnlyMemory<byte> NotANumber = new byte[]
            {
                (byte)'N', (byte)'a', (byte)'N'
            };
            private static readonly ReadOnlyMemory<byte> PositiveInfinity = new byte[]
            {
                (byte)'I', (byte)'n', (byte)'f', (byte)'i', (byte)'n', (byte)'i', (byte)'t', (byte)'y'
            };
            private static readonly ReadOnlyMemory<byte> NegativeInfinity = new byte[]
            {
                (byte)'-', (byte)'I', (byte)'n', (byte)'f', (byte)'i', (byte)'n', (byte)'i', (byte)'t', (byte)'y'
            };
            private static readonly ReadOnlyMemory<byte> TrueString = new byte[]
            {
                (byte)'t', (byte)'r', (byte)'u', (byte)'e'
            };
            private static readonly ReadOnlyMemory<byte> FalseString = new byte[]
            {
                (byte)'f', (byte)'a', (byte)'l', (byte)'s', (byte)'e'
            };
            private static readonly ReadOnlyMemory<byte> NullString = new byte[]
            {
                (byte)'n', (byte)'u', (byte)'l', (byte)'l'
            };

            private readonly JsonTextMemoryWriter jsonTextMemoryWriter;

            /// <summary>
            /// Whether we are writing the first value of an array or object
            /// </summary>
            private bool firstValue;

            /// <summary>
            /// Initializes a new instance of the JsonTextWriter class.
            /// </summary>
            /// <param name="skipValidation">Whether or not to skip validation</param>
            public JsonTextWriter(bool skipValidation)
                : base(skipValidation)
            {
                this.firstValue = true;
                this.jsonTextMemoryWriter = new JsonTextMemoryWriter();
            }

            /// <summary>
            /// Gets the SerializationFormat of the JsonWriter.
            /// </summary>
            public override JsonSerializationFormat SerializationFormat
            {
                get
                {
                    return JsonSerializationFormat.Text;
                }
            }

            /// <summary>
            /// Gets the current length of the internal buffer.
            /// </summary>
            public override long CurrentLength
            {
                get
                {
                    return this.jsonTextMemoryWriter.Position;
                }
            }

            /// <summary>
            /// Writes the object start symbol to internal buffer.
            /// </summary>
            public override void WriteObjectStart()
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.BeginObject);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(ObjectStartToken);
                this.firstValue = true;
            }

            /// <summary>
            /// Writes the object end symbol to the internal buffer.
            /// </summary>
            public override void WriteObjectEnd()
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.EndObject);
                this.jsonTextMemoryWriter.Write(ObjectEndToken);

                // We reset firstValue here because we'll need a separator before the next value
                this.firstValue = false;
            }

            /// <summary>
            /// Writes the array start symbol to the internal buffer.
            /// </summary>
            public override void WriteArrayStart()
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.BeginArray);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(ArrayStartToken);
                this.firstValue = true;
            }

            /// <summary>
            /// Writes the array end symbol to the internal buffer.
            /// </summary>
            public override void WriteArrayEnd()
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.EndArray);
                this.jsonTextMemoryWriter.Write(ArrayEndToken);

                // We reset firstValue here because we'll need a separator before the next value
                this.firstValue = false;
            }

            /// <summary>
            /// Writes a field name to the the internal buffer.
            /// </summary>
            /// <param name="fieldName">The name of the field to write.</param>
            public override void WriteFieldName(string fieldName)
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.FieldName);
                this.PrefixMemberSeparator();

                // no separator after property name
                this.firstValue = true;

                this.jsonTextMemoryWriter.Write(PropertyStartToken);
                this.WriteEscapedString(fieldName);
                this.jsonTextMemoryWriter.Write(PropertyEndToken);

                this.jsonTextMemoryWriter.Write(ValueSeperatorToken);
            }

            /// <summary>
            /// Writes a string to the internal buffer.
            /// </summary>
            /// <param name="value">The value of the string to write.</param>
            public override void WriteStringValue(string value)
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.String);
                this.PrefixMemberSeparator();

                this.jsonTextMemoryWriter.Write(StringStartToken);
                this.WriteEscapedString(value);
                this.jsonTextMemoryWriter.Write(StringEndToken);
            }

            /// <summary>
            /// Writes a number to the internal buffer.
            /// </summary>
            /// <param name="value">The value of the number to write.</param>
            public override void WriteNumberValue(Number64 value)
            {
                if (value.IsInteger)
                {
                    this.WriteIntegerInternal(Number64.ToLong(value));
                }
                else
                {
                    this.WriteDoubleInternal(Number64.ToDouble(value));
                }
            }

            /// <summary>
            /// Writes a boolean to the internal buffer.
            /// </summary>
            /// <param name="value">The value of the boolean to write.</param>
            public override void WriteBoolValue(bool value)
            {
                this.JsonObjectState.RegisterToken(value ? JsonTokenType.True : JsonTokenType.False);
                this.PrefixMemberSeparator();

                if (value)
                {
                    this.jsonTextMemoryWriter.Write(TrueString.Span);
                }
                else
                {
                    this.jsonTextMemoryWriter.Write(FalseString.Span);
                }
            }

            /// <summary>
            /// Writes a null to the internal buffer.
            /// </summary>
            public override void WriteNullValue()
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.Null);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(NullString.Span);
            }

            public override void WriteInt8Value(sbyte value)
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.Int8);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(Int8TokenPrefix);
                this.jsonTextMemoryWriter.Write(value);
            }

            public override void WriteInt16Value(short value)
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.Int16);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(Int16TokenPrefix);
                this.jsonTextMemoryWriter.Write(value);
            }

            public override void WriteInt32Value(int value)
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.Int32);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(Int32TokenPrefix);
                this.jsonTextMemoryWriter.Write(value);
            }

            public override void WriteInt64Value(long value)
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.Int64);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(Int32TokenPrefix);
                this.jsonTextMemoryWriter.Write(Int32TokenPrefix);
                this.jsonTextMemoryWriter.Write(value);
            }

            public override void WriteFloat32Value(float value)
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.Float32);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(FloatTokenPrefix);
                this.jsonTextMemoryWriter.Write(value);
            }

            public override void WriteFloat64Value(double value)
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.Float64);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(DoubleTokenPrefix);
                this.jsonTextMemoryWriter.Write(value);
            }

            public override void WriteUInt32Value(uint value)
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.UInt32);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(UnsignedTokenPrefix);
                this.jsonTextMemoryWriter.Write(Int32TokenPrefix);
                this.jsonTextMemoryWriter.Write(value);
            }

            public override void WriteGuidValue(Guid value)
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.Guid);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(GuidTokenPrefix);
                this.jsonTextMemoryWriter.Write(value);
            }

            public override void WriteBinaryValue(ReadOnlySpan<byte> value)
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.Binary);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(BinaryTokenPrefix);
                this.jsonTextMemoryWriter.WriteBinaryAsBase64(value);
            }

            /// <summary>
            /// Gets the result of the JsonWriter.
            /// </summary>
            /// <returns>The result of the JsonWriter as an array of bytes.</returns>
            public override ReadOnlyMemory<byte> GetResult()
            {
                return this.jsonTextMemoryWriter.Buffer.Slice(
                    0,
                    this.jsonTextMemoryWriter.Position);
            }

            /// <summary>
            /// Writes a raw json token to the internal buffer.
            /// </summary>
            /// <param name="jsonTokenType">The JsonTokenType of the rawJsonToken</param>
            /// <param name="rawJsonToken">The raw json token.</param>
            protected override void WriteRawJsonToken(
                JsonTokenType jsonTokenType,
                ReadOnlySpan<byte> rawJsonToken)
            {
                this.JsonObjectState.RegisterToken(jsonTokenType);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(rawJsonToken);
            }

            /// <summary>
            /// Writes an integer to the internal buffer.
            /// </summary>
            /// <param name="value">The value of the integer to write.</param>
            private void WriteIntegerInternal(long value)
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.Number);
                this.PrefixMemberSeparator();
                this.jsonTextMemoryWriter.Write(value);
            }

            /// <summary>
            /// Writes an integer to the internal buffer.
            /// </summary>
            /// <param name="value">The value of the integer to write.</param>
            private void WriteDoubleInternal(double value)
            {
                this.JsonObjectState.RegisterToken(JsonTokenType.Number);
                this.PrefixMemberSeparator();
                if (double.IsNaN(value))
                {
                    this.jsonTextMemoryWriter.Write(StringStartToken);
                    this.jsonTextMemoryWriter.Write(NotANumber.Span);
                    this.jsonTextMemoryWriter.Write(StringEndToken);
                }
                else if (double.IsNegativeInfinity(value))
                {
                    this.jsonTextMemoryWriter.Write(StringStartToken);
                    this.jsonTextMemoryWriter.Write(NegativeInfinity.Span);
                    this.jsonTextMemoryWriter.Write(StringEndToken);
                }
                else if (double.IsPositiveInfinity(value))
                {
                    this.jsonTextMemoryWriter.Write(StringStartToken);
                    this.jsonTextMemoryWriter.Write(PositiveInfinity.Span);
                    this.jsonTextMemoryWriter.Write(StringEndToken);
                }
                else
                {
                    this.jsonTextMemoryWriter.Write(value);
                }
            }

            /// <summary>
            /// Will insert a member separator token if one is needed.
            /// </summary>
            private void PrefixMemberSeparator()
            {
                if (!this.firstValue)
                {
                    this.jsonTextMemoryWriter.Write(MemberSeperatorToken);
                }

                this.firstValue = false;
            }

            private bool RequiresEscapeSequence(char value)
            {
                switch (value)
                {
                    case '\\':
                    case '"':
                    case '/':
                    case '\b':
                    case '\f':
                    case '\n':
                    case '\r':
                    case '\t':
                        return true;
                    default:
                        return value < ' ';
                }
            }

            private void WriteEscapedString(string value)
            {
                // Escape the string if needed
                string escapedString;
                if (!value.Any(character => JsonTextWriter.CharacterNeedsEscaping(character)))
                {
                    // No escaping needed;
                    escapedString = value;
                }
                else
                {
                    StringBuilder stringBuilder = new StringBuilder();
                    int readOffset = 0;
                    while (readOffset != value.Length)
                    {
                        if (!this.RequiresEscapeSequence(value[readOffset]))
                        {
                            // Just write the character as is
                            stringBuilder.Append(value[readOffset++]);
                        }
                        else
                        {
                            char characterToEscape = value[readOffset++];
                            char escapeSequence = default;
                            switch (characterToEscape)
                            {
                                case '\\':
                                    escapeSequence = '\\';
                                    break;
                                case '"':
                                    escapeSequence = '"';
                                    break;
                                case '/':
                                    escapeSequence = '/';
                                    break;
                                case '\b':
                                    escapeSequence = 'b';
                                    break;
                                case '\f':
                                    escapeSequence = 'f';
                                    break;
                                case '\n':
                                    escapeSequence = 'n';
                                    break;
                                case '\r':
                                    escapeSequence = 'r';
                                    break;
                                case '\t':
                                    escapeSequence = 't';
                                    break;
                            }

                            if (escapeSequence >= ' ')
                            {
                                // We got a special character
                                stringBuilder.Append('\\');
                                stringBuilder.Append(escapeSequence);
                            }
                            else
                            {
                                // We got a control character (U+0000 through U+001F).
                                stringBuilder.AppendFormat(
                                    CultureInfo.InvariantCulture,
                                    "\\u{0:X4}",
                                    (int)characterToEscape);
                            }
                        }
                    }

                    escapedString = stringBuilder.ToString();
                }

                // Convert to UTF8
                byte[] utf8String = Encoding.UTF8.GetBytes(escapedString);
                this.jsonTextMemoryWriter.Write(utf8String);
            }

            private static bool CharacterNeedsEscaping(char c)
            {
                const char DoubleQuote = '"';
                const char ReverseSolidus = '\\';
                const char Space = ' ';

                return (c == DoubleQuote) || (c == ReverseSolidus) || (c < Space);
            }

            private sealed class JsonTextMemoryWriter : JsonMemoryWriter
            {
                private static readonly StandardFormat floatFormat = new StandardFormat(
                    symbol: 'G');

                private static readonly StandardFormat doubleFormat = new StandardFormat(
                    symbol: 'G');

                public JsonTextMemoryWriter(int initialCapacity = 256)
                    : base(initialCapacity)
                {
                }

                public void Write(bool value)
                {
                    const int MaxBoolLength = 5;
                    this.EnsureRemainingBufferSpace(MaxBoolLength);
                    if (!Utf8Formatter.TryFormat(value, this.Cursor.Span, out int bytesWritten))
                    {
                        throw new InvalidOperationException($"Failed to {nameof(this.Write)}({typeof(bool).FullName}{value})");
                    }

                    this.Position += bytesWritten;
                }

                public void Write(byte value)
                {
                    this.EnsureRemainingBufferSpace(1);
                    this.Buffer.Span[this.Position] = value;
                    this.Position++;
                }

                public void Write(sbyte value)
                {
                    const int MaxInt8Length = 4;
                    this.EnsureRemainingBufferSpace(MaxInt8Length);
                    if (!Utf8Formatter.TryFormat(value, this.Cursor.Span, out int bytesWritten))
                    {
                        throw new InvalidOperationException($"Failed to {nameof(this.Write)}({typeof(sbyte).FullName}{value})");
                    }

                    this.Position += bytesWritten;
                }

                public void Write(short value)
                {
                    const int MaxInt16Length = 6;
                    this.EnsureRemainingBufferSpace(MaxInt16Length);
                    if (!Utf8Formatter.TryFormat(value, this.Cursor.Span, out int bytesWritten))
                    {
                        throw new InvalidOperationException($"Failed to {nameof(this.Write)}({typeof(short).FullName}{value})");
                    }

                    this.Position += bytesWritten;
                }

                public void Write(int value)
                {
                    const int MaxInt32Length = 11;
                    this.EnsureRemainingBufferSpace(MaxInt32Length);
                    if (!Utf8Formatter.TryFormat(value, this.Cursor.Span, out int bytesWritten))
                    {
                        throw new InvalidOperationException($"Failed to {nameof(this.Write)}({typeof(int).FullName}{value})");
                    }

                    this.Position += bytesWritten;
                }

                public void Write(uint value)
                {
                    const int MaxInt32Length = 11;
                    this.EnsureRemainingBufferSpace(MaxInt32Length);
                    if (!Utf8Formatter.TryFormat(value, this.Cursor.Span, out int bytesWritten))
                    {
                        throw new InvalidOperationException($"Failed to {nameof(this.Write)}({typeof(int).FullName}{value})");
                    }

                    this.Position += bytesWritten;
                }

                public void Write(long value)
                {
                    const int MaxInt64Length = 20;
                    this.EnsureRemainingBufferSpace(MaxInt64Length);
                    if (!Utf8Formatter.TryFormat(value, this.Cursor.Span, out int bytesWritten))
                    {
                        throw new InvalidOperationException($"Failed to {nameof(this.Write)}({typeof(long).FullName}{value})");
                    }

                    this.Position += bytesWritten;
                }

                public void Write(float value)
                {
                    const int MaxNumberLength = 32;
                    this.EnsureRemainingBufferSpace(MaxNumberLength);
                    if (!Utf8Formatter.TryFormat(value, this.Cursor.Span, out int bytesWritten, JsonTextMemoryWriter.floatFormat))
                    {
                        throw new InvalidOperationException($"Failed to {nameof(this.Write)}({typeof(double).FullName}{value})");
                    }

                    this.Position += bytesWritten;
                }

                public void Write(double value)
                {
                    const int MaxNumberLength = 32;
                    this.EnsureRemainingBufferSpace(MaxNumberLength);
                    if (!Utf8Formatter.TryFormat(value, this.Cursor.Span, out int bytesWritten, JsonTextMemoryWriter.doubleFormat))
                    {
                        throw new InvalidOperationException($"Failed to {nameof(this.Write)}({typeof(double).FullName}{value})");
                    }

                    this.Position += bytesWritten;
                }

                public void Write(Guid value)
                {
                    const int GuidLength = 38;
                    this.EnsureRemainingBufferSpace(GuidLength);
                    if (!Utf8Formatter.TryFormat(value, this.Cursor.Span, out int bytesWritten))
                    {
                        throw new InvalidOperationException($"Failed to {nameof(this.Write)}({typeof(double).FullName}{value})");
                    }

                    this.Position += bytesWritten;
                }

                public void WriteBinaryAsBase64(ReadOnlySpan<byte> binary)
                {
                    this.EnsureRemainingBufferSpace(Base64.GetMaxEncodedToUtf8Length(binary.Length));
                    Base64.EncodeToUtf8(binary, this.Cursor.Span, out int bytesConsumed, out int bytesWritten);

                    this.Position += bytesWritten;
                }
            }
        }
    }
}
