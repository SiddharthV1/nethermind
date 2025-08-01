// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Evm;
using Nethermind.JsonRpc.Modules.Rpc;
using Nethermind.JsonRpc.Modules.Subscribe;
using Newtonsoft.Json;
using Spectre.Console;
using System.Reflection;

namespace Nethermind.DocGen;

internal static class JsonRpcGenerator
{
    private static readonly string[] _assemblies = [
        "Nethermind.Consensus.Clique",
        "Nethermind.Era1",
        "Nethermind.Flashbots",
        "Nethermind.HealthChecks",
        "Nethermind.JsonRpc"
    ];
    private const string _objectTypeName = "_object_";

    internal static void Generate(string path)
    {
        path = Path.Join(path, "docs", "interacting", "json-rpc-ns");

        var excluded = new[] {
            typeof(IContextAwareRpcModule).FullName,
            typeof(IEvmRpcModule).FullName,
            typeof(IRpcModule).FullName,
            typeof(IRpcRpcModule).FullName,
            typeof(ISubscribeRpcModule).FullName
        };
        var types = _assemblies.SelectMany(a => Assembly.Load(a).GetTypes())
            .Where(t => t.IsInterface && typeof(IRpcModule).IsAssignableFrom(t) &&
                !excluded.Any(x => x is not null && (t.FullName?.Contains(x, StringComparison.Ordinal) ?? false)))
            .OrderBy(t => t.Name);

        foreach (var file in Directory.EnumerateFiles(path))
        {
            if (file.EndsWith(".md", StringComparison.Ordinal) &&
                // Skip eth_subscribe.md and eth_unsubscribe.md
                !file.EndsWith("subscribe.md", StringComparison.Ordinal))
            {
                File.Delete(file);
            }
        }

        var methodMap = new Dictionary<string, IEnumerable<MethodInfo>>();

        foreach (var type in types)
        {
            var attr = type.GetCustomAttribute<RpcModuleAttribute>();

            if (attr is null)
            {
                AnsiConsole.MarkupLine($"[yellow]{type.Name} module type is missing[/]");
                continue;
            }

            var ns = attr.ModuleType.ToLowerInvariant();
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public);

            if (!methodMap.TryAdd(ns, methods))
                methodMap[ns] = methodMap[ns].Concat(methods);
        }

        if (methodMap.TryGetValue("eth", out IEnumerable<MethodInfo>? ethMethods))
        {
            // Inject the `subscribe` methods into `eth`
            methodMap["eth"] = ethMethods!
                .Concat(typeof(ISubscribeRpcModule).GetMethods(BindingFlags.Instance | BindingFlags.Public));
        }

        var i = 0;

        foreach (var (ns, methods) in methodMap)
        {
            methodMap[ns] = methods.OrderBy(m => m.Name);

            WriteMarkdown(path, ns, methodMap[ns], i++);
        }
    }

    private static void WriteMarkdown(string path, string ns, IEnumerable<MethodInfo> methods, int sidebarIndex)
    {
        var fileName = Path.Join(path, $"{ns}.md");

        using var stream = File.Open(fileName, FileMode.Create);
        using var file = new StreamWriter(stream);
        file.NewLine = "\n";

        file.WriteLine($"""
            ---
            title: {ns} namespace
            sidebar_label: {ns}
            sidebar_position: {sidebarIndex}
            ---

            import Tabs from "@theme/Tabs";
            import TabItem from "@theme/TabItem";

            """);

        foreach (var method in methods)
        {
            var attr = method.GetCustomAttribute<JsonRpcMethodAttribute>();

            if (attr is null || !attr.IsImplemented)
                continue;

            if (method.Name.Equals("eth_subscribe", StringComparison.Ordinal) ||
                method.Name.Equals("eth_unsubscribe", StringComparison.Ordinal))
            {
                WriteFromFile(file, Path.Join(path, $"{method.Name}.md"));

                continue;
            }

            file.WriteLine($"""
                ### {method.Name}

                """);

            if (!string.IsNullOrEmpty(attr.Description))
                file.WriteLine($"""
                    {attr.Description}

                    """);

            file.WriteLine("""
                <Tabs>
                """);

            WriteParameters(file, method);
            WriteRequest(file, method);
            WriteResponse(file, method, attr);

            file.WriteLine($"""
                </Tabs>

                """);
        }

        file.Close();

        AnsiConsole.MarkupLine($"[green]Generated[/] {fileName}");
    }

    private static void WriteParameters(StreamWriter file, MethodInfo method)
    {
        var parameters = method.GetParameters();

        if (parameters.Length == 0)
            return;

        file.WriteLine($"""
            <TabItem value="params" label="Parameters">

            """);

        var i = 1;

        foreach (var p in parameters)
        {
            var attr = p.GetCustomAttribute<JsonRpcParameterAttribute>();

            file.Write($"{i++}. `{p.Name}`: ");

            WriteExpandedType(file, p.ParameterType, 2);

            file.WriteLine();
        }

        file.WriteLine("""

            </TabItem>
            """);
    }

    private static void WriteRequest(StreamWriter file, MethodInfo method)
    {
        var parameters = string.Join(", ", method.GetParameters().Select(p => p.Name));

        file.WriteLine($$"""
            <TabItem value="request" label="Request" default>

            ```bash
            curl localhost:8545 \
              -X POST \
              -H "Content-Type: application/json" \
              --data '{
                  "jsonrpc": "2.0",
                  "id": 0,
                  "method": "{{method.Name}}",
                  "params": [{{parameters}}]
                }'
            ```

            </TabItem>
            """);
    }

    private static void WriteResponse(StreamWriter file, MethodInfo method, JsonRpcMethodAttribute attr)
    {
        if (method.ReturnType == typeof(void))
            return;

        file.WriteLine("""
            <TabItem value="response" label="Response">

            """);

        if (!string.IsNullOrEmpty(attr.ResponseDescription))
            file.WriteLine($"""
                {attr.ResponseDescription}

                """);

        file.Write("""
            ```json
            {
              "jsonrpc": "2.0",
              "id": 0,
              "result": result
            }
            ```
            
            `result`: 
            """);

        WriteExpandedType(file, GetReturnType(method.ReturnType));

        file.WriteLine("""
            
            </TabItem>
            """);
    }

    private static void WriteExpandedType(StreamWriter file, Type type, int indentation = 0, bool omitTypeName = false, IEnumerable<string?>? parentTypes = null)
    {
        parentTypes ??= new List<string>();

        if (parentTypes.Any(a => type.FullName?.Equals(a, StringComparison.Ordinal) ?? false))
        {
            file.WriteLine($"{Indent(indentation + 2)}<!--[circular ref]-->");

            return;
        }

        var jsonType = GetJsonTypeName(type);

        if (!jsonType.Equals(_objectTypeName, StringComparison.Ordinal))
        {
            if (TryGetEnumerableItemType(type, out var itemType, out var isDictionary))
            {
                file.Write($"{(isDictionary ? "map" : "array")} of ");

                WriteExpandedType(file, itemType!);
            }
            else
                file.WriteLine(jsonType);

            return;
        }

        if (!omitTypeName)
            file.WriteLine(_objectTypeName);

        var properties = GetSerializableProperties(type);

        foreach (var prop in properties)
        {
            var propJsonType = GetJsonTypeName(prop.PropertyType);

            file.WriteLine($"{Indent(indentation + 2)}- `{GetSerializedName(prop)}`: {propJsonType}");

            if (propJsonType.Equals(_objectTypeName, StringComparison.Ordinal))
                WriteExpandedType(file, prop.PropertyType, indentation + 2, true, parentTypes.Append(type.FullName));
            else if (propJsonType.Contains($" of {_objectTypeName}", StringComparison.Ordinal) &&
                TryGetEnumerableItemType(prop.PropertyType, out var itemType, out var _))
                WriteExpandedType(file, itemType!, indentation + 2, true, parentTypes.Append(type.FullName));
        }
    }

    private static void WriteFromFile(StreamWriter file, string fileName)
    {
        file.Flush();

        using var sourceFile = File.OpenRead(fileName);

        try
        {
            sourceFile.CopyTo(file.BaseStream);
        }
        catch (Exception)
        {
            AnsiConsole.WriteLine($"[red]Failed copying from[/] {fileName}");
        }
    }

    private static string GetJsonTypeName(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type);

        if (underlyingType is not null)
            return GetJsonTypeName(underlyingType);

        if (type.IsEnum)
            return "_integer_";

        if (TryGetEnumerableItemType(type, out var itemType, out var isDictionary))
            return $"{(isDictionary ? "map" : "array")} of {GetJsonTypeName(itemType!)}";

        return type.Name switch
        {
            "Address" => "_string_ (address)",
            "BigInteger"
                or "Int32"
                or "Int64"
                or "Int64&"
                or "UInt64"
                or "UInt256" => "_string_ (hex integer)",
            "BlockParameter" => "_string_ (block number or hash or either of `earliest`, `finalized`, `latest`, `pending`, or `safe`)",
            "Bloom"
                or "Byte"
                or "Byte[]" => "_string_ (hex data)",
            "Boolean" => "_boolean_",
            "Hash256" => "_string_ (hash)",
            "String" => "_string_",
            "TxType" => "_string_ (transaction type)",
            _ => _objectTypeName
        };
    }

    private static Type GetReturnType(Type type)
    {
        var returnType = type.IsGenericType
            ? type.GetGenericTypeDefinition() == typeof(Task<>)
                ? type.GetGenericArguments()[0].GetGenericArguments()[0]
                : type.GetGenericArguments()[0]
            : type;

        return Nullable.GetUnderlyingType(returnType) ?? returnType;
    }

    private static IEnumerable<PropertyInfo> GetSerializableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .OrderBy(p => p.Name);

    private static string GetSerializedName(PropertyInfo prop) =>
        prop.GetCustomAttribute<JsonPropertyAttribute>()?.PropertyName
            ?? $"{prop.Name[0].ToString().ToLowerInvariant()}{prop.Name[1..]}"; // Ugly incomplete camel case

    private static string Indent(int depth) => string.Empty.PadLeft(depth, ' ');

    private static bool TryGetEnumerableItemType(Type type, out Type? itemType, out bool isDictionary)
    {
        if (type.IsArray && type.HasElementType)
        {
            var elementType = type.GetElementType();

            // Ignore a byte array as it is treated as a hex string
            if (elementType == typeof(byte))
            {
                itemType = null;
                isDictionary = false;

                return false;
            }

            itemType = type.GetElementType();
            isDictionary = false;

            return true;
        }

        if (type.IsInterface && type.IsGenericType)
        {
            if (type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                itemType = type.GetGenericArguments().Last();
                isDictionary = false;

                return true;
            }

            if (type.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            {
                itemType = type.GetGenericArguments().Last();
                isDictionary = true;

                return true;
            }
        }

        if (type.IsGenericType && type.GetInterfaces()
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        {
            itemType = type.GetGenericArguments().Last();
            isDictionary = type.GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

            return true;
        }

        itemType = null;
        isDictionary = false;

        return false;
    }
}
