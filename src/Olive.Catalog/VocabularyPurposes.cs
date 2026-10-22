using YamlDotNet.RepresentationModel;

namespace Olive.Catalog;

public static class VocabularyPurposes
{
    // Reads the `purposes` list from the vendored vocabulary (read-only; never edited).
    public static IReadOnlySet<string> Load(string path)
    {
        try
        {
            var stream = new YamlStream();
            using (var reader = new StreamReader(path))
            {
                stream.Load(reader);
            }

            if (stream.Documents.Count == 1
                && stream.Documents[0].RootNode is YamlMappingNode root
                && root.Children.TryGetValue(new YamlScalarNode("purposes"), out var node)
                && node is YamlSequenceNode seq)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in seq.Children)
                {
                    if (item is not YamlScalarNode { Value: { Length: > 0 } v })
                    {
                        throw new InvalidOperationException("Vocabulary purposes are invalid.");
                    }

                    set.Add(v);
                }

                if (set.Count > 0)
                {
                    return set;
                }
            }
        }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException or IOException or UnauthorizedAccessException)
        {
        }

        throw new InvalidOperationException("Vocabulary purposes are invalid.");
    }
}
