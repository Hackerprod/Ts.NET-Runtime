using TypeSharp.IR;
using TypeSharp.Semantics.Binder;
using TypeSharp.Syntax;
using TypeSharp.Syntax.Diagnostics;
using TypeSharp.Syntax.SyntaxTree;
using TypeSharp.VM.Bytecode;

namespace TypeSharp.Hosting.Compilation;

public sealed class CompilationDiagnostics
{
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Errors => _errors;
    public IReadOnlyList<string> Warnings => _warnings;
    public bool HasErrors => _errors.Count > 0;

    public void Error(string message) => _errors.Add(message);
    public void Warning(string message) => _warnings.Add(message);
}

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
    private readonly CompilationDiagnostics _diagnostics = new();
    private readonly Dictionary<string, CompilationUnit> _units = new();
    private readonly Dictionary<string, CompiledModule> _compiledModules = new();

    public CompilationDiagnostics Diagnostics => _diagnostics;
    public IReadOnlyDictionary<string, CompiledModule> CompiledModules => _compiledModules;

    public TypeScriptCompilation(string sourceRoot)
    {
        _sourceRoot = Path.GetFullPath(sourceRoot);
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
        return withoutExt.Replace('\\', '/').TrimStart('/');
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
                        _diagnostics.Error($"{moduleId}: {d}");
                    continue;
                }

                var unit = new CompilationUnit(file, moduleId, syntaxTree);

                CollectImportsAndExports(unit, syntaxTree);
                _units[moduleId] = unit;
            }
            catch (Exception ex)
            {
                _diagnostics.Error($"Failed to parse {file}: {ex.Message}");
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
        var unresolved = _units.Values
            .Where(u => u.Imports.Any(i => !_units.ContainsKey(i)))
            .ToList();

        foreach (var unit in unresolved)
        {
            var missing = unit.Imports.Where(i => !_units.ContainsKey(i)).ToList();
            foreach (var m in missing)
            {
                _diagnostics.Warning($"Module '{m}' not found (imported by '{unit.ModuleId}'). Cross-module imports not yet fully supported.");
            }
        }
    }

    public Dictionary<string, BoundSourceFile> Bind()
    {
        var boundFiles = new Dictionary<string, BoundSourceFile>();

        foreach (var (moduleId, unit) in _units)
        {
            try
            {
                var binder = new Binder();
                var boundTree = binder.Bind(unit.SyntaxTree);

                if (binder.Diagnostics.HasErrors)
                {
                    foreach (var d in binder.Diagnostics.GetErrors())
                        _diagnostics.Error($"{moduleId}: {d}");
                    continue;
                }

                boundFiles[moduleId] = boundTree;
            }
            catch (Exception ex)
            {
                _diagnostics.Error($"Failed to bind {moduleId}: {ex.Message}");
            }
        }

        return boundFiles;
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
                _diagnostics.Error($"Failed to compile {moduleId}: {ex.Message}");
            }
        }

        return _compiledModules;
    }
}
