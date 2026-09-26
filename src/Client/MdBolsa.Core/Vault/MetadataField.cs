namespace MdBolsa.Core.Vault;

// One top-level frontmatter field, as read (never written) by FrontMatter.ReadFields.
public readonly record struct MetadataField(string Key, string Value);
