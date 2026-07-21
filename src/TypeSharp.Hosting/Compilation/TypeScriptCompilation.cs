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
    public List<ExportDeclarationSyntax> ExportDeclarations { get; } = new();
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
        if (IsExecutableTypeScriptFile(filePath))
            _sourceFiles.Add(Path.GetFullPath(filePath));
    }

    public void AddSourceDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        var files = Directory.GetFiles(directory, "*.ts", SearchOption.AllDirectories)
            .Where(IsExecutableTypeScriptFile);
        _sourceFiles.AddRange(files.Select(f => Path.GetFullPath(f)));
    }

    public static bool IsExecutableTypeScriptFile(string filePath)
    {
        return filePath.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) &&
               !filePath.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase);
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
                if (!ValidateImportAttributes(unit, import))
                    continue;

                string importPath = ResolveImportPath(unit.FilePath, import.ModulePath, import.Range.Start);
                if (string.IsNullOrEmpty(importPath))
                    continue;
                string importModuleId = ComputeModuleId(importPath, _sourceRoot);
                unit.Imports.Add(importModuleId);
                unit.ImportMap[import.ModulePath] = importModuleId;
            }

            if (member is ExportDeclarationSyntax export)
            {
                unit.ExportDeclarations.Add(export);
                if (export.ModulePath != null)
                {
                    string exportPath = ResolveImportPath(unit.FilePath, export.ModulePath, export.Range.Start);
                    if (string.IsNullOrEmpty(exportPath))
                        continue;
                    string exportModuleId = ComputeModuleId(exportPath, _sourceRoot);
                    unit.Imports.Add(exportModuleId);
                    unit.ImportMap[export.ModulePath] = exportModuleId;
                }
            }

            if (member is DeclarationSyntax decl && decl.Modifiers.Any(m => m.Token.Kind == Syntax.TokenKind.ExportKeyword))
            {
                string? name = GetDeclarationName(decl);
                if (name != null)
                    unit.Exports.Add(decl.Modifiers.Any(m => m.Token.Kind == Syntax.TokenKind.DefaultKeyword) ? "default" : name);
            }

            if (member is VariableDeclarationSyntax { IsExported: true } variable)
            {
                unit.Exports.Add(variable.ExportName ?? variable.Name);
            }

            if (member is VariableDeclarationListSyntax list)
            {
                foreach (var declaration in list.Declarations.Where(d => d.IsExported))
                    unit.Exports.Add(declaration.ExportName ?? declaration.Name);
            }
        }
    }

    private bool ValidateImportAttributes(CompilationUnit unit, ImportDeclarationSyntax import)
    {
        foreach (var attribute in import.Attributes)
        {
            _diagnostics.Error(
                $"Unsupported import attribute '{attribute.Key}: {attribute.Value}' in '{unit.ModuleId}'",
                import.Range.Start,
                DiagnosticCode.TS3002);
            return false;
        }

        return true;
    }

    private string ResolveImportPath(string fromFile, string importPath, SourceLocation location)
    {
        if (!importPath.StartsWith("./", StringComparison.Ordinal) &&
            !importPath.StartsWith("../", StringComparison.Ordinal))
        {
            _diagnostics.Error(
                $"Only relative ESM imports are supported: '{importPath}'",
                location,
                DiagnosticCode.TS3002);
            return string.Empty;
        }

        var dir = Path.GetDirectoryName(fromFile) ?? "";
        var resolved = Path.GetFullPath(Path.Combine(dir, importPath));

        if (!resolved.EndsWith(".ts"))
            resolved += ".ts";

        var root = Path.GetFullPath(_sourceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullResolved = Path.GetFullPath(resolved);
        if (!fullResolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            _diagnostics.Error(
                $"Import '{importPath}' from '{ComputeModuleId(fromFile, _sourceRoot)}' escapes the source root",
                location,
                DiagnosticCode.TS3002);
            return string.Empty;
        }

        return fullResolved;
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
            var missing = unit.Imports.Distinct(StringComparer.Ordinal).Where(i => !_units.ContainsKey(i)).ToList();
            foreach (var m in missing)
            {
                var loc = unit.GetLocation();
                _diagnostics.Error(
                    $"Module '{m}' not found (imported by '{unit.ModuleId}')",
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
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
        return DfsCycle(unit.ModuleId, visiting, path);
    }

    private string? DfsCycle(string moduleId, HashSet<string> visiting, List<string> path)
    {
        if (visiting.Contains(moduleId))
        {
            var start = path.IndexOf(moduleId);
            var cycle = start >= 0 ? path.Skip(start).Concat(new[] { moduleId }) : path.Append(moduleId);
            return string.Join(" -> ", cycle);
        }

        if (!_units.TryGetValue(moduleId, out var unit))
            return null;

        visiting.Add(moduleId);
        path.Add(moduleId);
        foreach (var imp in unit.Imports)
        {
            var cycle = DfsCycle(imp, visiting, path);
            if (cycle != null) return cycle;
        }

        path.RemoveAt(path.Count - 1);
        visiting.Remove(moduleId);
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

                var exportsForModule = new Dictionary<string, Symbol>(binder.Exports, StringComparer.Ordinal);
                ApplyExportDeclarations(unit, binder.ModuleSymbols, moduleExports, exportsForModule);
                moduleExports[moduleId] = exportsForModule;
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

    private void ApplyExportDeclarations(
        CompilationUnit unit,
        IReadOnlyDictionary<string, Symbol> localSymbols,
        IReadOnlyDictionary<string, Dictionary<string, Symbol>> moduleExports,
        Dictionary<string, Symbol> exports)
    {
        foreach (var declaration in unit.ExportDeclarations)
        {
            if (declaration.ExportKind == ExportDeclarationKind.Star)
            {
                if (declaration.ModulePath == null ||
                    !unit.ImportMap.TryGetValue(declaration.ModulePath, out var starModuleId) ||
                    !moduleExports.TryGetValue(starModuleId, out var starExports))
                    continue;

                foreach (var (name, symbol) in starExports)
                {
                    if (name == "default")
                        continue;
                    if (exports.TryGetValue(name, out var existing) && !ReferenceEquals(existing, symbol))
                    {
                        _diagnostics.Error(
                            $"Ambiguous star re-export '{name}' in module '{unit.ModuleId}'",
                            declaration.Range.Start,
                            DiagnosticCode.TS3002);
                        continue;
                    }
                    exports[name] = symbol;
                }
                continue;
            }

            var sourceExports = localSymbols;
            if (declaration.ModulePath != null)
            {
                if (!unit.ImportMap.TryGetValue(declaration.ModulePath, out var sourceModuleId) ||
                    !moduleExports.TryGetValue(sourceModuleId, out var importedExports))
                    continue;
                sourceExports = importedExports;
            }

            foreach (var specifier in declaration.Specifiers)
            {
                var exportedName = specifier.Alias ?? specifier.Name;
                if (!sourceExports.TryGetValue(specifier.Name, out var symbol))
                {
                    _diagnostics.Error(
                        $"Cannot export '{specifier.Name}' from module '{unit.ModuleId}' because it is not defined",
                        specifier.Range.Start,
                        DiagnosticCode.TS3002);
                    continue;
                }

                exports[exportedName] = symbol;
            }
        }
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
                case BoundVariableDeclaration variable when variable.Symbol is LocalSymbol { IsExported: true }:
                    exports[variable.Symbol.Name] = variable.Symbol;
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
