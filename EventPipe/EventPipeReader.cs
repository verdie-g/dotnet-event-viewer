﻿using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using EventPipe.FastSerializer;

// ReSharper disable UnusedVariable

namespace EventPipe;

public sealed class EventPipeReader(Stream stream)
{
    private const int ReaderVersion = 4;

    private static ReadOnlySpan<byte> MagicBytes => "Nettrace"u8;
    private static ReadOnlySpan<byte> SerializerSignature => "!FastSerialization.1"u8;
    private static readonly object TrueBoolean = true;
    private static readonly object FalseBoolean = false;
    private static IReadOnlyDictionary<string, object> EmptyDictionary = new Dictionary<string, object>();

    private readonly HashSet<string> _internedStrings = [];
    private readonly Dictionary<byte, object> _internedByte = [];
    private readonly Dictionary<sbyte, object> _internedSByte = [];
    private readonly Dictionary<short, object> _internedInt16 = [];
    private readonly Dictionary<ushort, object> _internedUInt16 = [];

    private readonly Dictionary<int, EventMetadata> _eventMetadata = [];
    private readonly List<Event> _events = [];
    private readonly StackResolver _stackResolver = new();

    private IProgress<Progression>? _progress;

    private bool _headerRead;
    private long _readPosition;
    private TraceMetadata? _traceMetadata;

    /// <summary>
    /// The sequence point block notifies that the stack id sequence was reset to zero (i.e. new events can reuse
    /// a previously seen stack id). To deal with that, the stack traces of the read events could be resolved when
    /// a sequence point block is seen, but it's annoying to do because the symbols events might not have been read
    /// yet. Instead, custom stack ids are generated by adding the real stack id with the last stack id read before
    /// the last sequence point read.
    /// </summary>
    private int _lastStackIndex;
    private int _stackIndexOffset;

    public EventPipeReader AddProgress(IProgress<Progression> progress)
    {
        _progress = progress;
        return this;
    }

    public async Task<Trace> ReadFullTraceAsync()
    {
        Pipe pipe = new();

        _ = Task.Run(() => FillPipeWithStreamAsync(pipe.Writer, CancellationToken.None));

        var reader = pipe.Reader;
        while (true)
        {
            var result = await reader.ReadAsync();
            var buffer = result.Buffer;

            SequencePosition consumed = HandleBuffer(in buffer);

            reader.AdvanceTo(consumed, buffer.End);

            if (result.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync();

        // Sort the events because correlation algorithms expect a Stop event to appear after a Start.
        _events.Sort((x, y) => x.TimeStamp.CompareTo(y.TimeStamp));

        _stackResolver.ResolveEventStackTraces(_events);

        return new Trace(
            _traceMetadata!,
            _eventMetadata.Values.ToArray(),
            _events);
    }

    private SequencePosition HandleBuffer(in ReadOnlySequence<byte> buffer)
    {
        long oldReadPosition = _readPosition;
        FastSerializerSequenceReader reader = new(buffer, _readPosition);
        SequencePosition consumed = reader.Position;

        if (!_headerRead)
        {
            if (!TryReadHeader(ref reader))
            {
                return consumed;
            }

            _headerRead = true;
            consumed = reader.Position;
            _readPosition = oldReadPosition + reader.Consumed;
        }

        while (!reader.End && TryReadObject(ref reader))
        {
            consumed = reader.Position;
            _readPosition = oldReadPosition + reader.Consumed;

            _progress?.Report(new Progression(_readPosition, _events.Count));
        }

        return consumed;
    }

    private static bool TryReadHeader(ref FastSerializerSequenceReader reader)
    {
        if (!reader.TryReadBytes(MagicBytes.Length, out var magicBytesSeq))
        {
            return false;
        }

        Span<byte> magicBytes = stackalloc byte[MagicBytes.Length];
        magicBytesSeq.CopyTo(magicBytes);

        if (!magicBytes.SequenceEqual(MagicBytes))
        {
            throw new InvalidDataException("Stream is not in the event pipe format");
        }

        if (!reader.TryReadString(out var serializerSignatureSeq))
        {
            return false;
        }

        Span<byte> serializerSignature = stackalloc byte[SerializerSignature.Length];
        serializerSignatureSeq.CopyTo(serializerSignature);

        if (!serializerSignature.SequenceEqual(SerializerSignature))
        {
            throw new InvalidDataException("Unexpected serializer signature");
        }

        return true;
    }

    private bool TryReadObject(ref FastSerializerSequenceReader reader)
    {
        if (!TryReadByteAsEnum(ref reader, out FastSerializerTag tag))
        {
            return false;
        }

        // Last object is just a null ref tag.
        if (tag == FastSerializerTag.NullReference)
        {
            return true;
        }

        AssertTag(FastSerializerTag.BeginPrivateObject, tag);

        if (!TryReadSerializationType(ref reader, out var serializationType))
        {
            return false;
        }

        var blockRead = serializationType.Name == "Trace"
            ? TryReadTraceObject(ref reader)
            : TryReadBlock(ref reader, in serializationType);

        return blockRead
               && TryReadAndAssertTag(ref reader, FastSerializerTag.EndObject);
    }

    private bool TryReadSerializationType(
        ref FastSerializerSequenceReader reader,
        out SerializationType serializationType)
    {
        if (!TryReadAndAssertTag(ref reader, FastSerializerTag.BeginPrivateObject)
            || !TryReadAndAssertTag(ref reader, FastSerializerTag.NullReference)
            || !reader.TryReadInt32(out int objectVersion)
            || !reader.TryReadInt32(out int minReaderVersion)
            || !reader.TryReadInt32(out int typeNameLength)
            || !reader.TryReadBytes(typeNameLength, out var typeNameSeq)
            || !TryReadAndAssertTag(ref reader, FastSerializerTag.EndObject))
        {
            serializationType = default;
            return false;
        }

        // TODO: A custom dictionary allowing ReadOnlySequence for lookups could be built to avoid this allocation.
        string serializationTypeName = Encoding.UTF8.GetString(typeNameSeq);
        serializationType = new SerializationType(objectVersion, minReaderVersion, serializationTypeName);
        return true;
    }

    private bool TryReadTraceObject(ref FastSerializerSequenceReader reader)
    {
        if (!reader.TryReadInt16(out short year)
            || !reader.TryReadInt16(out short month)
            || !reader.TryReadInt16(out short dayOfWeek)
            || !reader.TryReadInt16(out short day)
            || !reader.TryReadInt16(out short hour)
            || !reader.TryReadInt16(out short minute)
            || !reader.TryReadInt16(out short second)
            || !reader.TryReadInt16(out short millisecond)
            || !reader.TryReadInt64(out long qpcSyncTime)
            || !reader.TryReadInt64(out long qpcFrequency)
            || !reader.TryReadInt32(out int pointerSize)
            || !reader.TryReadInt32(out int processId)
            || !reader.TryReadInt32(out int numberOfProcessors)
            || !reader.TryReadInt32(out int cpuSamplingRate))
        {
            return false;
        }

        var date = new DateTime(year, month, day, hour, minute, second, millisecond);
        _traceMetadata = new TraceMetadata(date, qpcSyncTime, qpcFrequency, pointerSize, processId, numberOfProcessors,
            cpuSamplingRate);

        return true;
    }

    private bool TryReadBlock(ref FastSerializerSequenceReader reader, in SerializationType serializationType)
    {
        if (!reader.TryReadInt32(out int blockSize)
            || !TryReadPadding(ref reader)
            || reader.Remaining < blockSize)
        {
            return false;
        }

        if (serializationType.MinReaderVersion > ReaderVersion)
        {
            reader.Advance(blockSize); // Skip the block for forward compatibility.
            return true;
        }

        long blockEndPosition = reader.AbsolutePosition + blockSize;

        switch (serializationType.Name)
        {
            case "StackBlock":
                ReadStackBlock(ref reader);
                break;
            case "MetadataBlock":
            case "EventBlock":
                ReadMetadataOrEventBlock(ref reader, blockSize);
                break;
            case "SPBlock":
                ReadSequencePointBlock(ref reader);
                break;
            default:
                reader.Advance(blockSize); // Skip the block for forward compatibility.
                break;
        }

        if (reader.AbsolutePosition != blockEndPosition)
        {
            throw new InvalidDataException($"{serializationType.Name} end was not reached at position {reader.AbsolutePosition}");
        }

        return true;
    }

    private void ReadStackBlock(ref FastSerializerSequenceReader reader)
    {
        int firstId = reader.ReadInt32();
        int count = reader.ReadInt32();

        int stackIndex = _lastStackIndex;
        for (int i = 0; i < count; i += 1)
        {
            int stackId = firstId + i;
            stackIndex = _stackIndexOffset + stackId;

            int stackSize = reader.ReadInt32();

            ReadOnlySpan<ulong> addresses;
            var unreadSpan = reader.UnreadSpan;
            if (unreadSpan.Length >= stackSize)
            {
                addresses = MemoryMarshal.Cast<byte, ulong>(unreadSpan[..stackSize]);
                _stackResolver.AddStackAddresses(stackIndex, addresses);
                reader.Advance(stackSize);
                continue;
            }

            var addressBytes = reader.ReadBytes(stackSize).ToArray();
            addresses = MemoryMarshal.Cast<byte, ulong>(addressBytes);
            _stackResolver.AddStackAddresses(stackIndex, addresses);

        }

        _lastStackIndex = stackIndex;
    }

    private void ReadMetadataOrEventBlock(ref FastSerializerSequenceReader reader, long blockSize)
    {
        long headerStartPosition = reader.AbsolutePosition;
        long blockEndPosition = headerStartPosition + blockSize;

        short headerSize = reader.ReadInt16();
        var flags = ReadInt16AsEnum<EventBlockFlags>(ref reader);
        long minTimestamp = reader.ReadInt64();
        long maxTimestamp = reader.ReadInt64();
        reader.Advance(headerSize - reader.AbsolutePosition + headerStartPosition);

        if (flags.HasFlag(EventBlockFlags.Compressed))
        {
            CompressedEventBlobState state = new();
            while (reader.AbsolutePosition < blockEndPosition)
            {
                ReadCompressedEventBlob(ref reader, ref state);
            }
        }
        else
        {
            while (reader.AbsolutePosition < blockEndPosition)
            {
                ReadUncompressedEventBlob(ref reader);
            }
        }
    }

    private void ReadUncompressedEventBlob(ref FastSerializerSequenceReader reader)
    {
        throw new NotImplementedException();
    }

    private void ReadCompressedEventBlob(ref FastSerializerSequenceReader reader,
        ref CompressedEventBlobState state)
    {
        var flags = ReadByteAsEnum<CompressedEventFlags>(ref reader);

        var metadataId = flags.HasFlag(CompressedEventFlags.HasMetadataId)
            ? reader.ReadVarInt32()
            : state.PreviousMetadataId;

        int sequenceNumber;
        long captureThreadId;
        int processorNumber;
        if (flags.HasFlag(CompressedEventFlags.HasSequenceNumberAndCaptureThreadIdAndProcessorNumber))
        {
            sequenceNumber = reader.ReadVarInt32();
            captureThreadId = reader.ReadVarInt64();
            processorNumber = reader.ReadVarInt32();

            sequenceNumber += state.PreviousSequenceNumber;
        }
        else
        {
            sequenceNumber = state.PreviousSequenceNumber;
            captureThreadId = state.PreviousCaptureThreadId;
            processorNumber = state.PreviousProcessorNumber;
        }

        if (metadataId != 0)
        {
            sequenceNumber += 1;
        }

        long threadId = flags.HasFlag(CompressedEventFlags.HasThreadId)
            ? reader.ReadVarInt64()
            : state.PreviousThreadId;

        int stackId = flags.HasFlag(CompressedEventFlags.HasStackId)
            ? reader.ReadVarInt32()
            : state.PreviousStackId;

        long timeStamp = reader.ReadVarInt64() + state.PreviousTimeStamp;

        Guid activityId = flags.HasFlag(CompressedEventFlags.HasActivityId)
            ? reader.ReadGuid()
            : state.PreviousActivityId;

        Guid relatedActivityId = flags.HasFlag(CompressedEventFlags.HasRelatedActivityId)
            ? reader.ReadGuid()
            : state.PreviousRelatedActivityId;

        bool isSorted = flags.HasFlag(CompressedEventFlags.IsSorted);

        int payloadSize = flags.HasFlag(CompressedEventFlags.HasPayloadSize)
            ? reader.ReadVarInt32()
            : state.PreviousPayloadSize;

        long payloadEndPosition = reader.AbsolutePosition + payloadSize;

        bool isMetadataBlock = metadataId == 0;
        if (isMetadataBlock)
        {
            var metadata = ReadEventMetadata(ref reader, payloadEndPosition);
            _eventMetadata[metadata.MetadataId] = metadata;
        }
        else
        {
            var metadata = _eventMetadata[metadataId];

            // Some events (e.g. from Microsoft-Windows-DotNETRuntimeRundown) don't define any fields but still have
            // a payload.
            IReadOnlyDictionary<string, object> payload;
            if (metadata.FieldDefinitions.Count == 0)
            {
                reader.Advance(payloadEndPosition - reader.AbsolutePosition);
                payload = EmptyDictionary;
            }
            else
            {
                payload = ReadEventPayload(ref reader, metadata);
            }

            int stackIndex = _stackIndexOffset + stackId;
            long timeStampRelativeNs = ConvertQpcToRelativeNs(timeStamp);
            Event evt = new(_events.Count, sequenceNumber, captureThreadId, threadId, stackIndex, timeStampRelativeNs,
                activityId, relatedActivityId, payload, metadata);
            _events.Add(evt);

            HandleSpecialEvent(evt);
        }

        if (reader.AbsolutePosition != payloadEndPosition)
        {
            throw new InvalidDataException($"Event blob payload end was not reached at position {reader.AbsolutePosition}");
        }

        state.PreviousMetadataId = metadataId;
        state.PreviousSequenceNumber = sequenceNumber;
        state.PreviousCaptureThreadId = captureThreadId;
        state.PreviousProcessorNumber = processorNumber;
        state.PreviousThreadId = threadId;
        state.PreviousStackId = stackId;
        state.PreviousTimeStamp = timeStamp;
        state.PreviousActivityId = activityId;
        state.PreviousRelatedActivityId = relatedActivityId;
        state.PreviousPayloadSize = payloadSize;
    }

    private EventMetadata ReadEventMetadata(
        ref FastSerializerSequenceReader reader,
        long metadataEndPosition)
    {
        int metadataId = reader.ReadInt32();
        string providerName = reader.ReadNullTerminatedString();
        int eventId = reader.ReadInt32();
        string eventName = reader.ReadNullTerminatedString();
        long keywords = reader.ReadInt64();
        int version = reader.ReadInt32();
        var level = ReadInt32AsEnum<EventLevel>(ref reader);

        providerName = InternString(providerName);

        IReadOnlyList<EventFieldDefinition> fieldDefinitions = ReadFieldDefinitions(ref reader, EventFieldDefinitionVersion.V1);

        EventOpcode? opCode = null;
        while (reader.AbsolutePosition < metadataEndPosition)
        {
            int tagPayloadBytes = reader.ReadInt32();
            var tag = ReadByteAsEnum<EventMetadataTag>(ref reader);

            long tagPayloadEndPosition = reader.AbsolutePosition + tagPayloadBytes;

            if (tag == EventMetadataTag.OpCode)
            {
                opCode = ReadByteAsEnum<EventOpcode>(ref reader);
            }
            else if (tag == EventMetadataTag.ParameterPayload)
            {
                if (fieldDefinitions.Count == 0)
                {
                    throw new InvalidDataException($"No V2 field definitions are expected after V1 field definitions at position {reader.AbsolutePosition}");
                }

                fieldDefinitions = ReadFieldDefinitions(ref reader, EventFieldDefinitionVersion.V2);
            }

            if (reader.AbsolutePosition != tagPayloadEndPosition)
            {
                throw new EndOfStreamException($"Event metadata tag end was not reached at position {reader.AbsolutePosition}");
            }
        }

        if (KnownEvent.All.TryGetValue(new KnownEvent.Key(providerName, eventId, version), out var knownMetadata))
        {
            eventName = knownMetadata.EventName;
            opCode = knownMetadata.Opcode;
            fieldDefinitions = knownMetadata.FieldDefinitions;
        }

        // If the metadata is neither complete nor hardcoded.
        if (eventName.Length == 0)
        {
            eventName = $"Event {eventId}";
        }

        return new EventMetadata(metadataId, providerName, eventId, eventName, (EventKeywords)keywords,
            version, level, opCode, fieldDefinitions);
    }

    private EventFieldDefinition[] ReadFieldDefinitions(
        ref FastSerializerSequenceReader reader,
        EventFieldDefinitionVersion version)
    {
        int fieldCount = reader.ReadInt32();
        if (fieldCount == 0)
        {
            return Array.Empty<EventFieldDefinition>();
        }

        var fieldDefinitions = new EventFieldDefinition[fieldCount];
        for (int i = 0; i < fieldCount; i += 1)
        {
            var typeCode = ReadInt32AsEnum<TypeCode>(ref reader);

            TypeCode arrayTypeCode = default;
            if (version == EventFieldDefinitionVersion.V2
                && typeCode == TypeCodeExtensions.Array)
            {
                arrayTypeCode = ReadInt32AsEnum<TypeCode>(ref reader);
            }

            EventFieldDefinition[]? subFieldDefinitions = null;
            if (typeCode == TypeCode.Object)
            {
                subFieldDefinitions = ReadFieldDefinitions(ref reader, version);
            }

            string fieldName = reader.ReadNullTerminatedString();
            fieldName = InternString(fieldName);

            TypeCode? nullableArrayTypeCode = arrayTypeCode == default ? null : arrayTypeCode;
            fieldDefinitions[i] = new EventFieldDefinition(fieldName, typeCode, nullableArrayTypeCode, subFieldDefinitions);
        }

        return fieldDefinitions;
    }

    private IReadOnlyDictionary<string, object> ReadEventPayload(
        ref FastSerializerSequenceReader reader,
        EventMetadata eventMetadata)
    {
        if (KnownEvent.All.TryGetValue(
                new KnownEvent.Key(eventMetadata.ProviderName, eventMetadata.EventId, eventMetadata.Version),
                out var knownEvent))
        {
            return knownEvent.Parse(ref reader);
        }

        return ReadEventPayload(ref reader, eventMetadata.FieldDefinitions);
    }

    private IReadOnlyDictionary<string, object> ReadEventPayload(
        ref FastSerializerSequenceReader reader,
        IReadOnlyList<EventFieldDefinition> fieldDefinitions)
    {
        // Cast to array to avoid the IEnumerator allocation.
        var fieldDefinitionsArr = (EventFieldDefinition[])fieldDefinitions;

        Dictionary<string, object> payload = new(capacity: fieldDefinitionsArr.Length);
        foreach (var fieldDefinition in fieldDefinitionsArr)
        {
            object value = ReadFieldValue(ref reader, fieldDefinition);
            payload[fieldDefinition.Name] = value;
        }

        return payload;
    }

    private object ReadFieldValue(
        ref FastSerializerSequenceReader reader,
        EventFieldDefinition fieldDefinition)
    {
        if (fieldDefinition.TypeCode == TypeCodeExtensions.Guid)
        {
            return reader.ReadGuid();
        }

        return fieldDefinition.TypeCode switch
        {
            TypeCode.Object => ReadEventPayload(ref reader, fieldDefinition.SubFieldDefinitions!),
            TypeCode.Boolean => InternBoolean(reader.ReadInt32() != 0),
            TypeCode.SByte => Intern((sbyte)reader.ReadInt32(), _internedSByte),
            TypeCode.Byte => Intern(reader.ReadByte(), _internedByte),
            TypeCode.Int16 => Intern(reader.ReadInt16(), _internedInt16),
            TypeCode.UInt16 => Intern(reader.ReadUInt16(), _internedUInt16),
            TypeCode.Int32 => reader.ReadInt32(),
            TypeCode.UInt32 => reader.ReadUInt32(),
            TypeCode.Int64 => reader.ReadInt64(),
            TypeCode.UInt64 => reader.ReadUInt64(),
            TypeCode.Single => reader.ReadSingle(),
            TypeCode.Double => reader.ReadDouble(),
            TypeCode.String => reader.ReadNullTerminatedString(),
            _ => throw new NotSupportedException($"Type {fieldDefinition.TypeCode} is not supported")
        };
    }

    private void HandleSpecialEvent(Event evt)
    {
        if (evt.Metadata.ProviderName == KnownEvent.RundownProvider)
        {
            switch (evt.Metadata.EventId)
            {
                case 144:
                {
                    var address = (ulong)evt.Payload["MethodStartAddress"];
                    var size = (uint)evt.Payload["MethodSize"];
                    var @namespace = (string)evt.Payload["MethodNamespace"];
                    var name = (string)evt.Payload["MethodName"];
                    var signature = (string)evt.Payload["MethodSignature"];
                    _stackResolver.AddMethodSymbolInfo(new MethodDescription(name, @namespace, signature, address, size));
                    break;
                }
            }
        }
    }

    private long ConvertQpcToRelativeNs(long timeStamp)
    {
        var traceMetadata = _traceMetadata;
        if (traceMetadata == null)
        {
            return 0;
        }

        return (long)((timeStamp - traceMetadata.QueryPerformanceCounterSyncTime)
            / (double)traceMetadata.QueryPerformanceCounterFrequency
            * 1_000_000_000);
    }

    private void ReadSequencePointBlock(ref FastSerializerSequenceReader reader)
    {
        long timeStamp = reader.ReadInt64();
        int threadCount = reader.ReadInt32();

        for (int i = 0; i < threadCount; i += 1)
        {
            ReadThreadSequencePoint(ref reader);
        }

        _stackIndexOffset = _lastStackIndex;
    }

    private static void ReadThreadSequencePoint(ref FastSerializerSequenceReader reader)
    {
        long threadId = reader.ReadInt64();
        int sequenceNumber = reader.ReadInt32();
    }

    private static TEnum ReadByteAsEnum<TEnum>(ref FastSerializerSequenceReader reader) where TEnum : Enum
    {
        return TryReadByteAsEnum<TEnum>(ref reader, out var value) ? value : throw new EndOfStreamException();
    }

    private static bool TryReadByteAsEnum<TEnum>(
        ref FastSerializerSequenceReader reader,
        [MaybeNullWhen(false)] out TEnum value) where TEnum : Enum
    {
        if (!reader.TryReadByte(out byte b))
        {
            value = default;
            return false;
        }

        value = Unsafe.As<byte, TEnum>(ref b);
        return true;
    }

    private TEnum ReadInt16AsEnum<TEnum>(ref FastSerializerSequenceReader reader)
    {
        short value = reader.ReadInt16();
        return Unsafe.As<short, TEnum>(ref value);
    }

    private static TEnum ReadInt32AsEnum<TEnum>(ref FastSerializerSequenceReader reader) where TEnum : Enum
    {
        return TryReadInt32AsEnum<TEnum>(ref reader, out var value) ? value : throw new EndOfStreamException();
    }

    private static bool TryReadInt32AsEnum<TEnum>(
        ref FastSerializerSequenceReader reader,
        [MaybeNullWhen(false)] out TEnum value) where TEnum : Enum
    {
        if (!reader.TryReadInt32(out int i))
        {
            value = default;
            return false;
        }

        value = Unsafe.As<int, TEnum>(ref i);
        return true;
    }

    private static bool TryReadAndAssertTag(ref FastSerializerSequenceReader reader, FastSerializerTag expectedTag)
    {
        if (!TryReadByteAsEnum(ref reader, out FastSerializerTag tag))
        {
            return false;
        }

        AssertTag(expectedTag, tag);
        return true;
    }

    private static void AssertTag(FastSerializerTag expectedTag, FastSerializerTag actualTag)
    {
        if (expectedTag != actualTag)
        {
            throw new InvalidDataException($"Expected tag '{expectedTag}' but got '{actualTag}'");
        }
    }

    private static void ReadPadding(ref FastSerializerSequenceReader reader)
    {
        if (!TryReadPadding(ref reader))
        {
            throw new EndOfStreamException();
        }
    }

    private static bool TryReadPadding(ref FastSerializerSequenceReader reader)
    {
        const int padding = 4;

        long position = reader.AbsolutePosition;
        if (position % padding == 0)
        {
            return true;
        }

        int bytesToSkip = (int)(padding - position % padding);
        Debug.Assert(bytesToSkip > 0);
        if (!reader.TryReadBytes(bytesToSkip, out var paddingSeq))
        {
            return false;
        }

        Debug.Assert(paddingSeq.ToArray().All(b => b == 0), "Padding is not zero");
        return true;
    }

    private string InternString(string str)
    {
        if (_internedStrings.TryGetValue(str, out string? cachedStr))
        {
            return cachedStr;
        }

        _internedStrings.Add(str);
        return str;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object InternBoolean(bool b)
    {
        return b ? TrueBoolean : FalseBoolean;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static object Intern<T>(T val, Dictionary<T, object> dictionary) where T : notnull
    {
        if (!dictionary.TryGetValue(val, out object? obj))
        {
            obj = val;
            dictionary[val] = obj;
        }

        return obj;
    }

    private async Task FillPipeWithStreamAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        const int minimumBufferSize = 64 * 1024;

        while (true)
        {
            var buffer = writer.GetMemory(minimumBufferSize);
            try
            {
                int bytesRead = await stream.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                writer.Advance(bytesRead);
            }
            catch (Exception e)
            {
                await writer.CompleteAsync(e);
                break;
            }

            var flushResult = await writer.FlushAsync(cancellationToken);
            if (flushResult.IsCompleted)
            {
                break;
            }
        }

        await writer.CompleteAsync();
    }

    public sealed class Progression
    {
        internal Progression(long bytesRead, int eventsRead)
        {
            BytesRead = bytesRead;
            EventsRead = eventsRead;
        }

        public long BytesRead { get; }
        public int EventsRead { get; }
    }

    private readonly record struct SerializationType(int ObjectVersion, int MinReaderVersion, string Name);

    [Flags]
    private enum EventBlockFlags : byte
    {
        Compressed = 1 << 0,
    }

    private struct CompressedEventBlobState
    {
        public int PreviousMetadataId { get; set; }
        public int PreviousSequenceNumber { get; set; }
        public long PreviousCaptureThreadId { get; set; }
        public int PreviousProcessorNumber { get; set; }
        public long PreviousThreadId { get; set; }
        public int PreviousStackId { get; set; }
        public long PreviousTimeStamp { get; set; }
        public Guid PreviousActivityId { get; set; }
        public Guid PreviousRelatedActivityId { get; set; }
        public int PreviousPayloadSize { get; set; }
    }

    [Flags]
    private enum CompressedEventFlags : byte
    {
        HasMetadataId = 1 << 0,
        HasSequenceNumberAndCaptureThreadIdAndProcessorNumber = 1 << 1,
        HasThreadId = 1 << 2,
        HasStackId = 1 << 3,
        HasActivityId = 1 << 4,
        HasRelatedActivityId = 1 << 5,
        IsSorted = 1 << 6,
        HasPayloadSize = 1 << 7,
    }

    private enum EventFieldDefinitionVersion
    {
        V1,
        V2,
    }

    private enum EventMetadataTag : byte
    {
        OpCode = 1,
        ParameterPayload = 2,
    }
}
