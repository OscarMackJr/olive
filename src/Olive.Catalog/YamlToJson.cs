using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Olive.Catalog;

// Converts a YamlStream node tree to JSON. Plain scalars follow the YAML 1.2 core schema; quoted scalars stay strings.
// YamlStream.Load rejects duplicate mapping keys. Anchors/aliases and multi-document streams are refused (fail closed).
internal static partial class YamlToJson
{
    private const int MaxDepth = 64;

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex IntPattern();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatPattern();

    public static JsonNode? Parse(string yaml)
    {
        var stream = new YamlStream();
        using (var reader = new StringReader(yaml))
        {
            stream.Load(reader);
        }

        if (stream.Documents.Count != 1)
        {
            throw new InvalidOperationException("Catalog is invalid.");
        }

        return Convert(stream.Documents[0].RootNode, 0);
    }

    private static JsonNode? Convert(YamlNode node, int depth)
    {
        if (depth > MaxDepth || !node.Anchor.IsEmpty)
        {
            throw new InvalidOperationException("Catalog is invalid.");
        }

        switch (node)
        {
            case YamlMappingNode map:
                var obj = new JsonObject();
                foreach (var (k, v) in map.Children)
                {
                    if (k is not YamlScalarNode { Value: { } key })
                    {
                        throw new InvalidOperationException("Catalog is invalid.");
                    }

                    obj.Add(key, Convert(v, depth + 1));
                }

                return obj;
            case YamlSequenceNode seq:
                var arr = new JsonArray();
                foreach (var item in seq.Children)
                {
                    arr.Add(Convert(item, depth + 1));
                }

                return arr;
            case YamlScalarNode scalar:
                return Scalar(scalar);
            default:
                throw new InvalidOperationException("Catalog is invalid.");
        }
    }

    private static JsonNode? Scalar(YamlScalarNode s)
    {
        var v = s.Value ?? "";
        if (s.Style != ScalarStyle.Plain)
        {
            return JsonValue.Create(v);
        }

        switch (v)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        if (IntPattern().IsMatch(v) && long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return JsonValue.Create(l);
        }

        if (FloatPattern().IsMatch(v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
        {
            return JsonValue.Create(d);
        }

        return JsonValue.Create(v);
    }
}
