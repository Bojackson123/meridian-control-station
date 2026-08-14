using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Mcs.Trace;

/// <summary>One <c>[Verifies("MCS-NNN")]</c>, and the class or method it sits on.</summary>
/// <param name="MethodName">
///   <see langword="null"/> for a class-level tag, which claims every test in the class rather
///   than one named method.
/// </param>
internal sealed record TagSite(
    string Assembly,
    string TypeFullName,
    string? MethodName,
    string RequirementId)
{
    public override string ToString() =>
        MethodName is null ? TypeFullName : $"{TypeFullName}.{MethodName}";
}

/// <summary>Reads the tags back out of the built test assemblies.</summary>
/// <remarks>
///   <para>
///     Metadata rather than source. A regex over <c>.cs</c> files would have been shorter and is a
///     second, approximate C# parser in a repository that already keeps one hand-written parser
///     honest at some cost; it also cannot tell a live attribute from a commented-out one, which
///     is the failure that matters here because it fails <em>towards</em> green.
///   </para>
///   <para>
///     Nothing is loaded or executed: <see cref="MetadataReader"/> reads the file as bytes, so
///     this needs none of a test assembly's dependencies to be present and cannot be affected by
///     a static constructor. That is also why the assemblies can travel between CI jobs as bare
///     <c>.dll</c> files with nothing beside them.
///   </para>
///   <para>
///     <c>Verifies.cs</c> is compiled <em>into</em> each suite, so an attribute's constructor is a
///     <see cref="MethodDefinitionHandle"/> in the same module -- not the
///     <see cref="MemberReferenceHandle"/> that a referenced attribute would produce. Handling only
///     the reference case is the usual shape of this code and would find nothing at all here, and
///     report it as a requirements table with no tests behind it.
///   </para>
/// </remarks>
internal static class VerifiesTags
{
    private const string AttributeTypeName = "VerifiesAttribute";

    internal static IReadOnlyList<TagSite> Read(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader pe = new(stream);

        if (!pe.HasMetadata)
        {
            return [];
        }

        MetadataReader reader = pe.GetMetadataReader();
        string assembly = reader.GetString(reader.GetAssemblyDefinition().Name);

        List<TagSite> tags = [];
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            string fullName = FullNameOf(reader, type);

            foreach (string id in RequirementIdsOn(reader, type.GetCustomAttributes()))
            {
                tags.Add(new TagSite(assembly, fullName, MethodName: null, id));
            }

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(methodHandle);
                string methodName = reader.GetString(method.Name);

                foreach (string id in RequirementIdsOn(reader, method.GetCustomAttributes()))
                {
                    tags.Add(new TagSite(assembly, fullName, methodName, id));
                }
            }
        }

        return tags;
    }

    private static IEnumerable<string> RequirementIdsOn(
        MetadataReader reader, CustomAttributeHandleCollection attributes)
    {
        foreach (CustomAttributeHandle handle in attributes)
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            if (NameOfAttributeType(reader, attribute) != AttributeTypeName)
            {
                continue;
            }

            //  The constructor takes one string, so the blob is the 0x0001 prolog followed by a
            //  SerString. Decoding it by hand rather than through CustomAttributeValue<T> avoids
            //  needing a type provider, which would need type resolution, which is the thing this
            //  class exists to do without.
            BlobReader blob = reader.GetBlobReader(attribute.Value);
            if (blob.Length < 2 || blob.ReadUInt16() != 0x0001)
            {
                continue;
            }

            if (blob.ReadSerializedString() is { } id)
            {
                yield return id;
            }
        }
    }

    private static string? NameOfAttributeType(MetadataReader reader, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MethodDefinition:
                MethodDefinition constructor =
                    reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                return reader.GetString(
                    reader.GetTypeDefinition(constructor.GetDeclaringType()).Name);

            case HandleKind.MemberReference:
                EntityHandle parent = reader
                    .GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                return parent.Kind is HandleKind.TypeReference
                    ? reader.GetString(reader.GetTypeReference((TypeReferenceHandle)parent).Name)
                    : null;

            default:
                return null;
        }
    }

    /// <summary>
    ///   The name xUnit reports for the class, which is <see cref="Type.FullName"/> — so a nested
    ///   class is <c>Namespace.Outer+Inner</c>, with a plus sign, and normalising that to a dot to
    ///   look tidier would make a nested test class match nothing in the results.
    /// </summary>
    private static string FullNameOf(MetadataReader reader, TypeDefinition type)
    {
        string name = reader.GetString(type.Name);

        if (type.IsNested)
        {
            TypeDefinition declaring = reader.GetTypeDefinition(type.GetDeclaringType());
            return $"{FullNameOf(reader, declaring)}+{name}";
        }

        string ns = reader.GetString(type.Namespace);
        return ns.Length == 0 ? name : $"{ns}.{name}";
    }

    /// <summary>Every <c>*.Tests.dll</c> under a directory, one per assembly name.</summary>
    internal static ImmutableSortedDictionary<string, string> FindAssemblies(string directory) =>
        Directory.EnumerateFiles(directory, "*.Tests.dll", SearchOption.AllDirectories)
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal)
            .ToImmutableSortedDictionary(g => g.Key!, g => g.First(), StringComparer.Ordinal);
}
