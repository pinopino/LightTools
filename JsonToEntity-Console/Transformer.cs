﻿using JsonToEntity.Core;
using JsonToEntity.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using FieldInfo = JsonToEntity.Model.FieldInfo;

namespace JsonToEntity
{
    public class Transformer
    {
        private string _output;
        private string _lang;
        private TemplateEngine _engine;
        private ITypeMapper _typeMapper;
        private ICommentFormatter _commentFormatter;

        public Transformer(string output, string template)
        {
            _output = output;
            _engine = new RazorEngine(template);
            _lang = _engine.ParseLangFromTemplate();
            SetTypeMapper();
            SetCommentFormatter();
        }

        public void Parse(string inputFile)
        {
            // 加载文件
            var content = File.ReadAllText(inputFile);
            var tree = CSharpSyntaxTree.ParseText(content);
            var root = (CompilationUnitSyntax)tree.GetRoot();

            // 编译dll
            var refPaths = new[] {
                typeof(object).GetTypeInfo().Assembly.Location
            };
            MetadataReference[] references = refPaths.Select(r => MetadataReference.CreateFromFile(r)).ToArray();
            var compilation = CSharpCompilation.Create(
                Path.GetRandomFileName(),
                syntaxTrees: new[] { tree },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var semModel = compilation.GetSemanticModel(tree);

            // 解析信息（只包含基本名称、注释信息）
            var list = new List<ClassInfo>();
            var ns = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().First();
            var ns_symbol = semModel.GetDeclaredSymbol(ns) as INamespaceSymbol;
            foreach (var item in GetAllTypes(compilation.GetCompilationNamespace(ns_symbol)))
            {
                var cinfo = new ClassInfo();
                cinfo.Name = item.Name;
                cinfo.RawComment = item.GetDocumentationCommentXml();
                cinfo.Fields = new List<FieldInfo>();
                foreach (var member in item.GetMembers().Where(p => p.Kind == SymbolKind.Property))
                {
                    var field = new FieldInfo();
                    field.Name = member.Name;
                    field.RawComment = member.GetDocumentationCommentXml();
                    cinfo.Fields.Add(field);
                }
                list.Add(cinfo);
            }

            // 再次解析，回填类型信息
            using (var ms = new MemoryStream())
            {
                EmitResult result = compilation.Emit(ms);
                if (result.Success)
                {
                    ms.Seek(0, SeekOrigin.Begin);
                    Assembly assembly = AssemblyLoadContext.Default.LoadFromStream(ms);
                    foreach (var type in assembly.ExportedTypes)
                    {
                        var cinfo = list.Find(p => p.Name == type.Name);
                        foreach (var prop in type.GetProperties())
                        {
                            var finfo = cinfo.Fields.Find(p => p.Name == prop.Name);
                            finfo.RawType = prop.PropertyType;
                        }
                    }
                }
                else
                {
                    IEnumerable<Diagnostic> failures = result.Diagnostics.Where(diagnostic =>
                        diagnostic.IsWarningAsError ||
                        diagnostic.Severity == DiagnosticSeverity.Error);
                    var err = new StringBuilder();
                    foreach (Diagnostic diagnostic in failures)
                        err.AppendLine($"\t{diagnostic.Id}: {diagnostic.GetMessage()}");
                    throw new Exception(err.ToString());
                }
            }

            var extension = string.Empty;
            var parsed = RenderClass(list, out extension);
            Output(inputFile, parsed, extension);
        }

        private IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol @namespace)
        {
            foreach (var type in @namespace.GetTypeMembers())
                foreach (var nestedType in GetNestedTypes(type))
                    yield return nestedType;

            foreach (var nestedNamespace in @namespace.GetNamespaceMembers())
                foreach (var type in GetAllTypes(nestedNamespace))
                    yield return type;
        }

        private IEnumerable<INamedTypeSymbol> GetNestedTypes(INamedTypeSymbol type)
        {
            yield return type;
            foreach (var nestedType in type.GetTypeMembers()
                .SelectMany(nestedType => GetNestedTypes(nestedType)))
                yield return nestedType;
        }

        private string RenderClass(List<ClassInfo> info, out string outFileExtension)
        {
            PreProcess(info);
            outFileExtension = _engine.GetOutFileExtension();
            return _engine.Render(info);
        }

        private void PreProcess(List<ClassInfo> list)
        {
            MapType(list);
            FormatComment(list);
        }

        private void MapType(List<ClassInfo> list)
        {
            foreach (var cinfo in list)
            {
                foreach (var finfo in cinfo.Fields)
                    finfo.TypeStr = _typeMapper.MapType(finfo);
            }
        }

        private void FormatComment(List<ClassInfo> list)
        {
            foreach (var cinfo in list)
            {
                cinfo.CommentStr = _commentFormatter.Format(cinfo.RawComment);
                foreach (var finfo in cinfo.Fields)
                    finfo.CommentStr = _commentFormatter.Format(finfo.RawComment);
            }
        }

        private void SetTypeMapper()
        {
            switch (_lang)
            {
                case "c#":
                case "csharp":
                    {
                        _typeMapper = new CSharpTypeMapper();
                    }
                    break;
            }
        }

        private void SetCommentFormatter()
        {
            switch (_lang)
            {
                case "c#":
                case "csharp":
                    {
                        _commentFormatter = new CSharpCommentFormatter();
                    }
                    break;
            }
        }

        private void Output(string input, string content, string extension)
        {
            var out_name = Path.Combine(
                _output,
                Path.GetFileNameWithoutExtension(input) + "_out." + extension);

            File.WriteAllBytes(out_name, Encoding.UTF8.GetBytes(content));
        }
    }
}
