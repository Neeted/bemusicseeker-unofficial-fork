# テストDLLを実行せず、メタデータだけからテスト名と直列化属性を取得する。
# 型のロードやstatic initializerの実行は、テスト集合の検出に不要なので行わない。

if ($null -eq ('VerificationTestMetadataReader' -as [type])) {
    $metadataReaderSource = @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

/// <summary>
/// コンパイル済みMSTestメソッドの静的な識別情報を表す。
/// </summary>
/// <remarks>
/// テストアセンブリをロードせず、メタデータ上の直接属性だけを保持する。
/// TestClassの継承や独自のテスト属性は解決せず、検出時に明示的に拒否する。
/// </remarks>
public sealed class VerificationTestMetadata
{
    /// <summary>
    /// 静的なテスト識別情報を初期化する。
    /// </summary>
    /// <param name="fullyQualifiedName">名前空間・型・メソッドを含む完全修飾名。</param>
    /// <param name="className">名前空間と型を含むテストクラス名。</param>
    /// <param name="typeDoNotParallelize">型へ直接付与された直列化属性の有無。</param>
    /// <param name="methodDoNotParallelize">メソッドへ直接付与された直列化属性の有無。</param>
    public VerificationTestMetadata(
        string fullyQualifiedName,
        string className,
        bool typeDoNotParallelize,
        bool methodDoNotParallelize)
    {
        FullyQualifiedName = fullyQualifiedName;
        ClassName = className;
        TypeDoNotParallelize = typeDoNotParallelize;
        MethodDoNotParallelize = methodDoNotParallelize;
    }

    /// <summary>
    /// 名前空間・型・メソッドを含む完全修飾名を取得する。
    /// </summary>
    public string FullyQualifiedName { get; }

    /// <summary>
    /// 名前空間と型を含むテストクラス名を取得する。
    /// </summary>
    public string ClassName { get; }

    /// <summary>
    /// 型へ直接付与された直列化属性の有無を取得する。
    /// </summary>
    public bool TypeDoNotParallelize { get; }

    /// <summary>
    /// メソッドへ直接付与された直列化属性の有無を取得する。
    /// </summary>
    public bool MethodDoNotParallelize { get; }

    /// <summary>
    /// 型またはメソッドへ直接付与された直列化属性の有無を取得する。
    /// </summary>
    public bool DoNotParallelize => TypeDoNotParallelize || MethodDoNotParallelize;
}

/// <summary>
/// テストアセンブリのCLIメタデータからMSTestメソッドを検出する。
/// </summary>
/// <remarks>
/// 型をロードしないため、検出時にstatic initializerやテストアセンブリの依存解決を実行しない。
/// </remarks>
public static class VerificationTestMetadataReader
{
    private const string UnitTestingNamespace = "Microsoft.VisualStudio.TestTools.UnitTesting";
    private const string TestClassAttribute = UnitTestingNamespace + ".TestClassAttribute";
    private const string TestMethodAttribute = UnitTestingNamespace + ".TestMethodAttribute";
    private const string DataTestMethodAttribute = UnitTestingNamespace + ".DataTestMethodAttribute";
    private const string DoNotParallelizeAttribute = UnitTestingNamespace + ".DoNotParallelizeAttribute";

    /// <summary>
    /// 指定したテストアセンブリから、完全修飾名順のメソッド情報を読み取る。
    /// </summary>
    /// <param name="assemblyPath">読み取るコンパイル済みテストアセンブリのパス。</param>
    /// <returns>完全修飾名のOrdinal順に並んだMSTestメソッド情報。</returns>
    /// <exception cref="InvalidDataException">
    /// CLIメタデータがない、直接のTestClass属性を持たないテストメソッドがある、
    /// 継承されたTestClass、または未対応のMSTestメソッド属性がある場合。
    /// </exception>
    public static VerificationTestMetadata[] Read(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            throw new InvalidDataException("The test assembly does not contain CLI metadata.");
        }

        MetadataReader metadataReader = peReader.GetMetadataReader();
        var methodDeclaringTypes = new Dictionary<MethodDefinitionHandle, TypeDefinitionHandle>();
        foreach (TypeDefinitionHandle typeHandle in metadataReader.TypeDefinitions)
        {
            TypeDefinition typeDefinition = metadataReader.GetTypeDefinition(typeHandle);
            foreach (MethodDefinitionHandle methodHandle in typeDefinition.GetMethods())
            {
                methodDeclaringTypes[methodHandle] = typeHandle;
            }
        }

        var tests = new List<VerificationTestMetadata>();
        foreach (TypeDefinitionHandle typeHandle in metadataReader.TypeDefinitions)
        {
            TypeDefinition typeDefinition = metadataReader.GetTypeDefinition(typeHandle);
            string className = GetTypeDefinitionName(metadataReader, typeHandle);
            bool typeIsTestClass = HasAttribute(
                metadataReader,
                typeDefinition.GetCustomAttributes(),
                TestClassAttribute,
                methodDeclaringTypes);
            if (typeIsTestClass && !HasSystemObjectBaseType(metadataReader, typeDefinition))
            {
                throw new InvalidDataException(
                    "MSTest TestClass inheritance is not supported by static discovery: " + className);
            }
            bool typeDoNotParallelize = HasAttribute(
                metadataReader,
                typeDefinition.GetCustomAttributes(),
                DoNotParallelizeAttribute,
                methodDeclaringTypes);

            foreach (MethodDefinitionHandle methodHandle in typeDefinition.GetMethods())
            {
                MethodDefinition methodDefinition = metadataReader.GetMethodDefinition(methodHandle);
                if (HasUnsupportedTestMethodAttribute(
                        metadataReader,
                        methodDefinition.GetCustomAttributes(),
                        methodDeclaringTypes))
                {
                    throw new InvalidDataException(
                        "Unsupported MSTest method attribute in static discovery: " + className);
                }
                if (!HasTestMethodAttribute(
                        metadataReader,
                        methodDefinition.GetCustomAttributes(),
                        methodDeclaringTypes))
                {
                    continue;
                }

                if (!typeIsTestClass)
                {
                    throw new InvalidDataException(
                        "MSTest method has no direct TestClass attribute on its declaring type: " + className);
                }

                string methodName = metadataReader.GetString(methodDefinition.Name);
                bool methodDoNotParallelize = HasAttribute(
                    metadataReader,
                    methodDefinition.GetCustomAttributes(),
                    DoNotParallelizeAttribute,
                    methodDeclaringTypes);
                tests.Add(new VerificationTestMetadata(
                    className + "." + methodName,
                    className,
                    typeDoNotParallelize,
                    methodDoNotParallelize));
            }
        }

        return tests
            .OrderBy(test => test.FullyQualifiedName, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool HasTestMethodAttribute(
        MetadataReader metadataReader,
        CustomAttributeHandleCollection attributes,
        IReadOnlyDictionary<MethodDefinitionHandle, TypeDefinitionHandle> methodDeclaringTypes)
    {
        return HasAttribute(metadataReader, attributes, TestMethodAttribute, methodDeclaringTypes)
            || HasAttribute(metadataReader, attributes, DataTestMethodAttribute, methodDeclaringTypes);
    }

    private static bool HasUnsupportedTestMethodAttribute(
        MetadataReader metadataReader,
        CustomAttributeHandleCollection attributes,
        IReadOnlyDictionary<MethodDefinitionHandle, TypeDefinitionHandle> methodDeclaringTypes)
    {
        foreach (CustomAttributeHandle attributeHandle in attributes)
        {
            CustomAttribute attribute = metadataReader.GetCustomAttribute(attributeHandle);
            string attributeTypeName = GetAttributeTypeName(
                metadataReader,
                attribute.Constructor,
                methodDeclaringTypes);
            if (attributeTypeName.StartsWith(UnitTestingNamespace + ".", StringComparison.Ordinal)
                && attributeTypeName.EndsWith("TestMethodAttribute", StringComparison.Ordinal)
                && !string.Equals(attributeTypeName, TestMethodAttribute, StringComparison.Ordinal)
                && !string.Equals(attributeTypeName, DataTestMethodAttribute, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSystemObjectBaseType(
        MetadataReader metadataReader,
        TypeDefinition typeDefinition)
    {
        return !typeDefinition.BaseType.IsNil
            && string.Equals(
                GetTypeName(metadataReader, typeDefinition.BaseType),
                "System.Object",
                StringComparison.Ordinal);
    }

    private static bool HasAttribute(
        MetadataReader metadataReader,
        CustomAttributeHandleCollection attributes,
        string expectedTypeName,
        IReadOnlyDictionary<MethodDefinitionHandle, TypeDefinitionHandle> methodDeclaringTypes)
    {
        foreach (CustomAttributeHandle attributeHandle in attributes)
        {
            CustomAttribute attribute = metadataReader.GetCustomAttribute(attributeHandle);
            if (string.Equals(
                    GetAttributeTypeName(metadataReader, attribute.Constructor, methodDeclaringTypes),
                    expectedTypeName,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetAttributeTypeName(
        MetadataReader metadataReader,
        EntityHandle constructor,
        IReadOnlyDictionary<MethodDefinitionHandle, TypeDefinitionHandle> methodDeclaringTypes)
    {
        EntityHandle typeHandle;
        if (constructor.Kind == HandleKind.MemberReference)
        {
            typeHandle = metadataReader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
        }
        else if (constructor.Kind == HandleKind.MethodDefinition
            && methodDeclaringTypes.TryGetValue(
                (MethodDefinitionHandle)constructor,
                out TypeDefinitionHandle declaringType))
        {
            typeHandle = declaringType;
        }
        else
        {
            return string.Empty;
        }

        return GetTypeName(metadataReader, typeHandle);
    }

    private static string GetTypeName(MetadataReader metadataReader, EntityHandle typeHandle)
    {
        return typeHandle.Kind switch
        {
            HandleKind.TypeDefinition => GetTypeDefinitionName(
                metadataReader,
                (TypeDefinitionHandle)typeHandle),
            HandleKind.TypeReference => GetTypeReferenceName(
                metadataReader,
                (TypeReferenceHandle)typeHandle),
            _ => string.Empty
        };
    }

    private static string GetTypeDefinitionName(
        MetadataReader metadataReader,
        TypeDefinitionHandle typeHandle)
    {
        TypeDefinition typeDefinition = metadataReader.GetTypeDefinition(typeHandle);
        string name = metadataReader.GetString(typeDefinition.Name);
        if (typeDefinition.IsNested)
        {
            return GetTypeDefinitionName(metadataReader, typeDefinition.GetDeclaringType()) + "+" + name;
        }

        string namespaceName = metadataReader.GetString(typeDefinition.Namespace);
        return string.IsNullOrEmpty(namespaceName) ? name : namespaceName + "." + name;
    }

    private static string GetTypeReferenceName(
        MetadataReader metadataReader,
        TypeReferenceHandle typeHandle)
    {
        TypeReference typeReference = metadataReader.GetTypeReference(typeHandle);
        string name = metadataReader.GetString(typeReference.Name);
        if (typeReference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return GetTypeReferenceName(
                metadataReader,
                (TypeReferenceHandle)typeReference.ResolutionScope) + "+" + name;
        }

        string namespaceName = metadataReader.GetString(typeReference.Namespace);
        return string.IsNullOrEmpty(namespaceName) ? name : namespaceName + "." + name;
    }
}
'@

    # PowerShellの実行環境が標準参照を用意するため、ここでテストDLLをロードせずに
    # MetadataReader/PEReaderを含む参照済みアセンブリだけを使ってコンパイルする。
    Add-Type -TypeDefinition $metadataReaderSource -Language CSharp
}

function Get-VerificationTestMetadata {
    param(
        [Parameter(Mandatory)]
        [string]$AssemblyPath
    )

    $resolvedAssemblyPath = [IO.Path]::GetFullPath($AssemblyPath)
    if (-not (Test-Path -LiteralPath $resolvedAssemblyPath -PathType Leaf)) {
        throw "Compiled test assembly is missing: $resolvedAssemblyPath"
    }

    return @([VerificationTestMetadataReader]::Read($resolvedAssemblyPath))
}
