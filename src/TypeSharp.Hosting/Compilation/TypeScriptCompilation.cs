using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Semantics.Symbols;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Diagnostics;
using TypeSharp.Syntax.SyntaxTree;
using TypeSharp.VM.Bytecode;

namespace TypeSharp.Hosting.Compilation;

public sealed class CompiledModule
{
    public string ModuleId { get; }
    public string SourcePath { get; }
    public string DisplayName { get; }
    public BytecodeModule Bytecode { get; }
    public BoundSourceFile? BoundTree { get; }

    public CompiledModule(string moduleId, string sourcePath, string displayName,
        BytecodeModule bytecode, BoundSourceFile? boundTree)
    {
        ModuleId = moduleId;
        SourcePath = sourcePath;
        DisplayName = displayName;
        Bytecode = bytecode;
        BoundTree = boundTree;
    }
}

public sealed class CompilationUnit
{
    public string FilePath { get; }
    public string ModuleId { get; }
    public SourceFileSyntax SyntaxTree { get; }
    public List<string> Imports { get; } = new();
    public List<string> Exports { get; } = new();
    public Dictionary<string, string> ImportMap { get; } = new();
    public SourceLocation GetLocation() => new(FilePath, 1, 1, 0);

    public CompilationUnit(string filePath, string moduleId, SourceFileSyntax syntaxTree)
    {
        FilePath = filePath;
        ModuleId = moduleId;
        SyntaxTree = syntaxTree;
    }
}

public sealed class TypeScriptCompilation
{
    private readonly string _sourceRoot;
    private readonly List<string> _sourceFiles = new();
    private readonly DiagnosticBag _diagnostics = new();
    private readonly Dictionary<string, CompilationUnit> _units = new();
    private readonly Dictionary<string, CompiledModule> _compiledModules = new();
    private readonly IReadOnlyDictionary<string, Symbol>? _globalSymbols;

    public DiagnosticBag Diagnostics => _diagnostics;
    public IReadOnlyDictionary<string, CompiledModule> CompiledModules => _compiledModules;

    public TypeScriptCompilation(string sourceRoot, IReadOnlyDictionary<string, Symbol>? globalSymbols = null)
    {
        _sourceRoot = Path.GetFullPath(sourceRoot);
        _globalSymbols = globalSymbols;
    }

    public void AddSourceFile(string filePath)
    {
        _sourceFiles.Add(Path.GetFullPath(filePath));
    }

    public void AddSourceDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var files = Directory.GetFiles(directory, "*.ts", SearchOption.AllDirectories);
        _sourceFiles.AddRange(files.Select(f => Path.GetFullPath(f)));
    }

    public static string ComputeModuleId(string filePath, string sourceRoot)
    {
        var fullRoot = Path.GetFullPath(sourceRoot);
        var fullPath = Path.GetFullPath(filePath);

        var relative = Path.GetRelativePath(fullRoot, fullPath);
        var withoutExt = Path.ChangeExtension(relative, null);
        var moduleId = withoutExt.Replace('\\', '/').TrimStart('/');
        if (moduleId.EndsWith("/main", StringComparison.Ordinal))
            return moduleId[..^5];
        if (moduleId == "main")
            return Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return moduleId;
    }

    public void Parse()
    {
        foreach (var file in _sourceFiles)
        {
            string moduleId = ComputeModuleId(file, _sourceRoot);

            try
            {
                string source = File.ReadAllText(file);
                var lexer = new Lexer(source, file);
                var tokens = lexer.Tokenize();

                var parser = new TypeSharp.Syntax.Parser.Parser(tokens);
                var syntaxTree = parser.Parse(file);

                if (parser.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                {
                    foreach (var d in parser.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        var diag = new Diagnostic(DiagnosticSeverity.Error, d.Message, d.Location, d.Code)
                        {
                            ModuleId = moduleId
                        };
                        _diagnostics.Add(diag);
                    }
                    continue;
                }

                var unit = new CompilationUnit(file, moduleId, syntaxTree);

                CollectImportsAndExports(unit, syntaxTree);
                _units[moduleId] = unit;
            }
            catch (Exception ex)
            {
                var loc = new SourceLocation(file, 1, 1, 0);
                _diagnostics.Error($"Failed to parse {Path.GetFileName(file)}: {ex.Message}", loc,
                    DiagnosticCode.TS3001);
            }
        }
    }

    private void CollectImportsAndExports(CompilationUnit unit, SourceFileSyntax syntaxTree)
    {
        foreach (var member in syntaxTree.Members)
        {
            if (member is ImportDeclarationSyntax import)
            {
                string importPath = ResolveImportPath(unit.FilePath, import.ModulePath);
                string importModuleId = ComputeModuleId(importPath, _sourceRoot);
                unit.Imports.Add(importModuleId);
                unit.ImportMap[import.ModulePath] = importModuleId;
            }

            if (member is DeclarationSyntax decl && decl.Modifiers.Any(m => m.Token.Kind == Syntax.TokenKind.ExportKeyword))
            {
                string name = GetDeclarationName(decl);
                if (name != null)
                    unit.Exports.Add(name);
            }
        }
    }

    private static string ResolveImportPath(string fromFile, string importPath)
    {
        var dir = Path.GetDirectoryName(fromFile) ?? "";
        var resolved = Path.GetFullPath(Path.Combine(dir, importPath));

        if (!resolved.EndsWith(".ts"))
            resolved += ".ts";

        return resolved;
    }

    private static string? GetDeclarationName(DeclarationSyntax decl)
    {
        return decl switch
        {
            FunctionDeclarationSyntax func => func.Name,
            ClassDeclarationSyntax cls => cls.Name,
            InterfaceDeclarationSyntax iface => iface.Name,
            EnumDeclarationSyntax en => en.Name,
            TypeAliasDeclarationSyntax alias => alias.Name,
            _ => null
        };
    }

    public void ResolveImports()
    {
        foreach (var unit in _units.Values)
        {
            var missing = unit.Imports.Where(i => !_units.ContainsKey(i)).ToList();
            foreach (var m in missing)
            {
                var loc = unit.GetLocation();
                _diagnostics.Warning(
                    $"Module '{m}' not found (imported by '{unit.ModuleId}'). Cross-module imports not yet fully supported.",
                    loc, DiagnosticCode.TS3002);
            }

            var cycles = DetectCycles(unit);
            if (cycles != null)
            {
                var loc = unit.GetLocation();
                _diagnostics.Error(
                    $"Circular dependency detected: {cycles}",
                    loc, DiagnosticCode.TS3003);
            }
        }
    }

    private string? DetectCycles(CompilationUnit unit)
    {
        var visited = new HashSet<string>();
        return DfsCycle(unit.ModuleId, visited);
    }

    private string? DfsCycle(string moduleId, HashSet<string> visited)
    {
        if (!visited.Add(moduleId))
            return moduleId;
        if (!_units.TryGetValue(moduleId, out var unit))
            return null;

        foreach (var imp in unit.Imports)
        {
            var cycle = DfsCycle(imp, visited);
            if (cycle != null) return cycle;
        }

        visited.Remove(moduleId);
        return null;
    }

    public Dictionary<string, BoundSourceFile> Bind()
    {
        var boundFiles = new Dictionary<string, BoundSourceFile>();
        var moduleExports = new Dictionary<string, Dictionary<string, Symbol>>();
        var sortedModules = TopologicalSort();

        foreach (var moduleId in sortedModules)
        {
            if (!_units.TryGetValue(moduleId, out var unit))
                continue;

            try
            {
                var binder = new Binder();
                if (_globalSymbols != null)
                    binder.AddGlobalSymbols(_globalSymbols);

                foreach (var (rawPath, modId) in unit.ImportMap)
                {
                    if (moduleExports.TryGetValue(modId, out var exports))
                        binder.AddImportedSymbols(rawPath, exports);
                }

                var boundTree = binder.Bind(unit.SyntaxTree);

                if (binder.Diagnostics.HasErrors)
                {
                    foreach (var d in binder.Diagnostics.GetErrors())
                    {
                        var diag = new Diagnostic(DiagnosticSeverity.Error, d.Message, d.Location, d.Code)
                        {
                            ModuleId = moduleId
                        };
                        _diagnostics.Add(diag);
                    }
                    continue;
                }

                foreach (var d in binder.Diagnostics.GetWarnings())
                {
                    var diag = new Diagnostic(DiagnosticSeverity.Warning, d.Message, d.Location, d.Code)
                    {
                        ModuleId = moduleId
                    };
                    _diagnostics.Add(diag);
                }

                moduleExports[moduleId] = ExtractExports(boundTree);
                boundFiles[moduleId] = boundTree;
            }
            catch (Exception ex)
            {
                var loc = unit.GetLocation();
                _diagnostics.Error($"Failed to bind '{moduleId}': {ex.Message}", loc, DiagnosticCode.TS3004);
            }
        }

        return boundFiles;
    }

    private List<string> TopologicalSort()
    {
        var visited = new HashSet<string>();
        var result = new List<string>();

        foreach (var moduleId in _units.Keys)
            Visit(moduleId, visited, result);

        return result;

        void Visit(string id, HashSet<string> vis, List<string> res)
        {
            if (!vis.Add(id)) return;
            if (_units.TryGetValue(id, out var unit))
            {
                foreach (var imp in unit.Imports)
                    Visit(imp, vis, res);
            }
            res.Add(id);
        }
    }

    private static Dictionary<string, Symbol> ExtractExports(BoundSourceFile boundTree)
    {
        var exports = new Dictionary<string, Symbol>();

        foreach (var member in boundTree.Members)
        {
            switch (member)
            {
                case BoundFunctionDeclaration func when func.Symbol.IsExported:
                    exports[func.Symbol.Name] = func.Symbol;
                    break;
                case BoundClassDeclaration cls when cls.Symbol.IsExported:
                    exports[cls.Symbol.Name] = cls.Symbol;
                    break;
                case BoundInterfaceDeclaration iface when iface.Symbol.IsExported:
                    exports[iface.Symbol.Name] = iface.Symbol;
                    break;
                case BoundEnumDeclaration en when en.Symbol.IsExported:
                    exports[en.Symbol.Name] = en.Symbol;
                    break;
            }
        }

        return exports;
    }

    public Dictionary<string, CompiledModule> Compile()
    {
        Parse();

        if (_diagnostics.HasErrors)
            return _compiledModules;

        ResolveImports();

        var boundFiles = Bind();

        if (_diagnostics.HasErrors)
            return _compiledModules;

        foreach (var (moduleId, boundTree) in boundFiles)
        {
            try
            {
                var irGen = new IRGenerator();
                var moduleIR = irGen.Generate(boundTree);

                var pipeline = new TypeSharp.IR.Optimizations.IRPipeline();
                foreach (var func in moduleIR.Functions)
                    pipeline.Optimize(func);

                var bytecode = BytecodeCompiler.Compile(moduleIR);
                var unit = _units[moduleId];

                var displayName = Path.GetFileNameWithoutExtension(unit.FilePath);
                var compiled = new CompiledModule(moduleId, unit.FilePath, displayName, bytecode, boundTree);
                _compiledModules[moduleId] = compiled;
            }
            catch (Exception ex)
            {
                var loc = _units.TryGetValue(moduleId, out var u) ? u.GetLocation() : new SourceLocation(moduleId, 1, 1, 0);
                _diagnostics.Error($"Failed to compile '{moduleId}': {ex.Message}", loc, DiagnosticCode.TS4001);
            }
        }

        return _compiledModules;
    }
}
