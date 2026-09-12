using System;
using System.Globalization;
using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace ServerManager.Configuration;

// Bounds the event stream before representation-model allocation and rejects
// YAML features that the server configuration formats do not support.
internal sealed class BoundedYamlParser : IParser
{
    private readonly Parser _inner;
    private readonly int _maximumEvents;
    private readonly int _maximumDepth;
    private readonly Func<string, InvalidDataException> _invalid;
    private int _events, _depth, _documents;

    internal BoundedYamlParser(
        TextReader reader,
        int maximumEvents,
        int maximumDepth,
        Func<string, InvalidDataException> invalid)
    {
        _inner = new Parser(reader);
        _maximumEvents = maximumEvents;
        _maximumDepth = maximumDepth;
        _invalid = invalid;
    }

    public ParsingEvent? Current => _inner.Current;

    public bool MoveNext()
    {
        if (!_inner.MoveNext()) return false;
        if (++_events > _maximumEvents) throw _invalid("YAML event limit exceeded");

        ParsingEvent? value = Current;
        if (value is AnchorAlias ||
            value is NodeEvent node && (!node.Anchor.IsEmpty || !node.Tag.IsEmpty))
            throw _invalid("YAML anchors, aliases and explicit tags are not supported");
        if (value is DocumentStart && ++_documents > 1)
            throw _invalid("multiple YAML documents are not supported");

        if (value is MappingStart || value is SequenceStart)
        {
            if (++_depth > _maximumDepth)
                throw _invalid(
                    "YAML nesting exceeds " +
                    _maximumDepth.ToString(CultureInfo.InvariantCulture) +
                    " levels");
        }
        else if (value is MappingEnd || value is SequenceEnd)
            --_depth;

        return true;
    }
}
