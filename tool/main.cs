// dotnet create console -n <name>
// dotnet build

/*
plan:
    - !!support boost (external libs download via cmake fetch)
        - cmake fetch
    - make better intellisence for conan
        - mb make links in repo root OR in package dir
    - auto name exe/lib (lib with prefix)
    - do not process already built items
    - cross-platform home dir obtain
    - restrict commiting symlinks
*/

using System.CommandLine;
using YamlDotNet.Serialization;
using System.Diagnostics;
using SmartFormat;

class ProjectSpec
{
    public enum Type
    {
        lib,    ///< most common case, type may be omitted
        exe,
        ext     ///< external dependency (package)
    }

    public enum Vendor
    {
        conan,
        cppget
    }

    public Type type { get; set; } = Type.lib;
    public string? name { get; set; }

    public string[] deps {get; set; } = new string[0];

    public string? version { get; set; }

    public Vendor? vendor{ get; set; }

    /// <summary>
    ///  Conan specifics
    /// </summary>
    public string? alias { get; set; }

    /// <summary>
    ///  CppGet specifics
    /// </summary>
    public string? url { get; set; }
}

class CMakeTemplate
{
    public class Context
    {
        public string? ProjectName { get; set; }
        public string? BuildRoot { get; set; }
        public string? SrcRoot { get; set; }

        public string? SrcPaths { get; set; }

        public string? DepLibNames { get; set; }
        public string? DepLibPaths { get; set; }

        public string? DepPackageNames { get; set; }
        public string? DepPackagePaths { get; set; }

        public string? DepFetchNames { get; set; }
        public string? DepFetchPaths { get; set; }
    }

    static string ExeTemplate = @"
cmake_minimum_required(VERSION 3.12)
project({ProjectName})
file(GLOB SOURCES {SrcPaths})

add_executable({ProjectName} $\{SOURCES\})

set(lib_names {DepLibNames})
set(lib_paths {DepLibPaths})
set(fetch_names {DepFetchNames})
set(fetch_paths {DepFetchPaths})
set(LIB_DEPS)

foreach(name path IN ZIP_LISTS fetch_names fetch_paths)
    include($\{path\})
    list(APPEND LIB_DEPS $\{name\}::$\{name\})
endforeach()

foreach(name path IN ZIP_LISTS lib_names lib_paths)
    find_library(LIB_$\{name\}
        NAMES $\{name\}
        PATHS $\{path\}
        NO_DEFAULT_PATH
    )

    if (LIB_$\{name\})
        list(APPEND LIB_DEPS $\{LIB_$\{name\}\})
    else()
        message(FATAL_ERROR ""$\{name\} not found in path $\{path\}."")
    endif()
endforeach()

set(pkg_names {DepPackageNames})
set(pkg_paths {DepPackagePaths})
set(CMAKE_PREFIX_PATH {DepPackagePaths})

foreach(name path IN ZIP_LISTS pkg_names pkg_paths)
    find_package(
        $\{name\} REQUIRED
        PATHS $\{path\}
        NO_DEFAULT_PATH
    )
    if (name)
        list(APPEND LIB_DEPS $\{name\}::$\{name\})
    else()
        message(FATAL_ERROR ""$\{name\} not found in path $\{path\}."")
    endif()
endforeach()

target_link_libraries({ProjectName} PRIVATE $\{LIB_DEPS\})
target_include_directories({ProjectName} PUBLIC
    {SrcRoot}
    $\{CMAKE_CURRENT_SOURCE_DIR\}
)
";

    static string LibTemplate = @"
cmake_minimum_required(VERSION 3.12)
project({ProjectName})
file(GLOB SOURCES _/*.cpp _/*.c)

add_library({ProjectName} STATIC $\{SOURCES\})
target_include_directories({ProjectName} PUBLIC {SrcRoot} $\{CMAKE_CURRENT_SOURCE_DIR\})
";

    static public string ComposeExe(Context context)
    {
        return Smart.Format(ExeTemplate, context);
    }

    static public string ComposeLib(Context context)
    {
        return Smart.Format(LibTemplate, context);
    }
}

class ConanTemplate
{
    public class Context
    {
        public string? Package { get; set; }
        public string? Version { get; set; }
    }

    static public string Compose(Context context)
    {
        return Smart.Format(PackageTemplate, context);
    }

    static string PackageTemplate = @"
[requires]
{Package}/{Version}

[generators]
CMakeDeps
CMakeToolchain
";
}

class CppGetTemplate
{
    public class Context
    {
        public string? Package { get; set; }
        public string? Version { get; set; }
        public string? Url { get; set; }
    }

    static public string Compose(Context context)
    {
        return Smart.Format(PackageTemplate, context);
    }

    static string PackageTemplate = @"
include(FetchContent)
cmake_policy(SET CMP0135 NEW)
FetchContent_Declare(
    {Package}
    URL ""{Url}/{Version}.tar.gz""
)

FetchContent_MakeAvailable({Package})
";
}

class Fs
{
    public delegate bool DirectoryVisitor(string path);

    public static void TraverseDirectories(string root, DirectoryVisitor visitor)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (!visitor(dir))
            {
                continue;
            }

            foreach (string subDir in Directory.GetDirectories(dir))
            {
                stack.Push(subDir);
            }
        }
    }

    public static void CreateSymbolicLink(string symlinkPath, string targetPath)
    {
        if (File.Exists(symlinkPath) || Directory.Exists(symlinkPath))
        {
            if (IsSymbolicLink(symlinkPath))
            {
                File.Delete(symlinkPath);
            }
            else
            {
                throw new Exception($"Cannot create symlink {symlinkPath}: file exists and not a symlink");
            }
        }

        File.CreateSymbolicLink(symlinkPath, targetPath);
    }

    public static bool IsSymbolicLink(string directoryPath)
    {
        FileAttributes attributes = File.GetAttributes(directoryPath);
        return (attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
    }
}

class Env
{
    public string ArtifactDir { get { return "_"; }}
    public string BuildRoot { get { return $"{ServiceDir}/build"; }}
    public readonly string SourceRoot;

    public static Env CreateOnHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new Env(Path.Combine(home, ".jb"));
    }

    public Env(string serviceDir)
    {
        ServiceDir = serviceDir;
        SourceRoot = GetSourceRoot();
    }

    public string GetProjectPath(string projectDir)
    {
        return Path.Combine(projectDir, ProjectConfigFileName);
    }

    public bool IsProjectRoot(string dir)
    {
        string cfgPath = GetProjectPath(dir);
        return Path.Exists(cfgPath);
    }

    public (string, bool) GetProjectDirectory(string startDir)
    {
        string? curDir = startDir;
        while (curDir != null)
        {
            if (IsProjectRoot(curDir))
            {
                return (curDir, false);
            }

            if (curDir == SourceRoot)
            {
                return (startDir, true);
            }

            curDir = Directory.GetParent(curDir)?.FullName;
        }

        throw new Exception($"Neither project file nor source root was found");
    }

    public string GetAbsoluteSourcePath(string srcRootRelativePath)
    {
        return Path.Combine(SourceRoot, srcRootRelativePath);
    }

    public string GetRelativeSourcePath(string srcRootAbsPath)
    {
        return Path.GetRelativePath(SourceRoot, srcRootAbsPath);
    }

    public string GetBuildPath(string srcRelPath)
    {
        return Path.Combine(BuildRoot, srcRelPath);
    }

    public string MirrorSourceToBuild(string srcRelDir)
    {
        var targetPath = GetBuildPath(srcRelDir);
        if (!Directory.Exists(targetPath))
        {
            Directory.CreateDirectory(targetPath);
        }

        return targetPath;
    }

    public void LinkArtifacts(string targetPath, string sourceDir)
    {
        var artifactDir = Path.Combine(sourceDir, ArtifactDir);
        if (Directory.Exists(targetPath))
        {
            /// link directory target directly to artifact-dir name
            Fs.CreateSymbolicLink(artifactDir, targetPath);
            return;
        }

        if (!Directory.Exists(artifactDir))
        {
            Directory.CreateDirectory(artifactDir);
        }

        var artifactPath = Path.Combine(artifactDir, Path.GetFileName(targetPath));
        Fs.CreateSymbolicLink(artifactPath, targetPath);
    }

    public string GetSourceRoot()
    {
        var currentDir = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(currentDir, ".jb.root")))
        {
            var parentDir = Directory.GetParent(currentDir);
            if (parentDir == null)
            {
                throw new Exception("Outside of repository");
            }

            currentDir = parentDir.FullName;
        }

        return currentDir;
    }

    public bool IsProjectRootDir(string dir)
    {
        return File.Exists(Path.Combine(dir, ProjectConfigFileName));
    }

    const string ProjectConfigFileName = "jb.yaml";

    readonly string ServiceDir;
}

class BuildNode
{
    public readonly string Path;

    public readonly string SourcePath;
    public readonly string BuildPath;

    public readonly string CMakeIncludePath;
    public readonly ProjectSpec Project;

    public readonly string Name;
    public readonly string Alias;

    public List<BuildNode> Dependers;

    public List<BuildNode> Dependees;

    public BuildNode(string path, Env env)
    {
        Path = path;
        SourcePath = env.GetAbsoluteSourcePath(path);
        BuildPath = env.GetBuildPath(path);
        var projectFilePath = env.GetProjectPath(SourcePath);
        Dependers = new List<BuildNode>();
        Dependees = new List<BuildNode>();
        var data = File.ReadAllText(projectFilePath);
        var deserializer = new DeserializerBuilder().Build();
        Project = deserializer.Deserialize<ProjectSpec>(data);
        Name = Project.name != null
            ? Project.name
            : Project.name = System.IO.Path.GetFileName(path);
        Alias = Project.alias != null
            ? Project.alias
            : Name;

        CMakeIncludePath = System.IO.Path.Combine(BuildPath, $"{Name}.cmake");
    }

    public List<BuildNode> CollectTransitiveLibraryDependers()
    {
        var result = new List<BuildNode>();
        var used = new HashSet<string>();
        result.Add(this);

        int i = 0;
        while (i < result.Count)
        {
            foreach(var dep in result[i].Dependers)
            {
                if (dep.Project.type != ProjectSpec.Type.exe && !used.Contains(dep.Path))
                {
                    used.Add(dep.Path);
                    result.Add(dep);
                }
            }

            ++i;
        }

        return result.GetRange(1, result.Count - 1);
    }
}


class CycleDetectingDfs
{
    public CycleDetectingDfs(BuildNode root, HashSet<string> visited)
    {
        DfsStack.Push(root);
        Visited = visited;
    }

    public void Push(BuildNode node)
    {
        if (CycleKeys.Contains(node.Path))
        {
            var cycle = new List<string>() {node.Path};
            while(CycleStack.Count > 0)
            {
                cycle.Add(CycleStack.Pop());
            }

            cycle.Reverse();
            var cycleStr = string.Join(" -> ", cycle);
            throw new Exception($"Cycle detected: {cycleStr}");
        }

        DfsStack.Push(node);
    }

    public BuildNode? Pop()
    {
        while (DfsStack.Count > 0)
        {
            var node = DfsStack.Pop();
            if (node == null)
            {
                CycleKeys.Remove(CycleStack.Pop());
                continue;
            }

            if (!Visited.Contains(node.Path))
            {
                return node;
            }
        }

        return null;
    }

    public void BeforeChildren(BuildNode parent)
    {
        CycleStack.Push(parent.Path);
        CycleKeys.Add(parent.Path);
        DfsStack.Push(null); /// children/parent separator
        Visited.Add(parent.Path);
    }

    Stack<BuildNode?> DfsStack = new Stack<BuildNode?>();
    HashSet<string> CycleKeys = new HashSet<string>();
    Stack<string> CycleStack = new Stack<string>();

    HashSet<string> Visited;
}

class BuildGraph
{
    public delegate void Visitor(BuildNode node);

    public BuildGraph(string startDir, Env env)
    {
        var nodeCache = new Dictionary<string, BuildNode>();
        var entryPoints = CollectEntryDirs(startDir, env);
        var topoRoots = new Queue<BuildNode>();
        var visited = new HashSet<string>();
        foreach(var entryPoint in entryPoints)
        {
            var srcDir = env.GetRelativeSourcePath(entryPoint);
            var entryNode = new BuildNode(srcDir, env);
            nodeCache.Add(entryNode.Path, entryNode);
            var dfs = new CycleDetectingDfs(entryNode, visited);
            BuildNode? targetNode;
            while ((targetNode = dfs.Pop()) != null)
            {
                dfs.BeforeChildren(targetNode);
                foreach(var depPath in targetNode.Project.deps)
                {
                    var depNode = nodeCache.GetValueOrDefault(depPath, new BuildNode(depPath, env));
                    dfs.Push(depNode);
                    targetNode.Dependers.Add(depNode);
                    depNode.Dependees.Add(targetNode);
                }

                if (targetNode.Dependers.Count == 0)
                {
                    topoRoots.Enqueue(targetNode);
                }
            }
        }

        TopoNodes = TopoSort(topoRoots);
    }

    public void Traverse(Visitor visitor)
    {
        foreach(var node in TopoNodes)
        {
            visitor(node);
        }
    }

    static List<string> CollectEntryDirs(string startDir, Env env)
    {
        var result = new List<string>();
        var (entryProjectDir, root) = env.GetProjectDirectory(startDir);
        /// todo: support Solution here
        if (root)
        {
            Fs.TraverseDirectories(entryProjectDir, (dir) =>
            {
                if (env.IsProjectRoot(dir))
                {
                    result.Add(dir);
                    return false;
                }

                return true;
            });
        }
        else
        {
            result.Add(entryProjectDir);
        }

        return result;
    }

    static List<BuildNode> TopoSort(Queue<BuildNode> queue)
    {
        var result = new List<BuildNode>();
        var inDegree = new Dictionary<string, int>();
        while(queue.Count > 0)
        {
            var node = queue.Dequeue();
            result.Add(node);
            foreach (var dep in node.Dependees) {
                if (!inDegree.ContainsKey(dep.Path))
                {
                    inDegree[dep.Path] = dep.Dependers.Count;
                }

                if (--inDegree[dep.Path] == 0)
                {
                    queue.Enqueue(dep);
                    inDegree.Remove(dep.Path);
                }
            }
        }

        return result;
    }

    readonly List<BuildNode> TopoNodes;
}

class Program
{
    static int Main(string[] args)
    {
        var env = Env.CreateOnHome();
        var verbose = new Option<bool>("--verbose", "Enable verbose output");
        var build = new Command("build");
        build.AddAlias("b");
        build.AddOption(verbose);
        build.SetHandler((verbose) => {
            try {
                Build(env, verbose);
            } catch (Exception err) {
                PrintFail(err.Message);
            }
        }, verbose);

        var clean = new Command("clean");
        clean.SetHandler((_) => {
            Clean(env);
        });

        var graph = new Command("graph");
        graph.SetHandler((_) => {
            try {
                Graph(env);
            } catch (Exception err) {
                PrintFail(err.Message);
            }
        });

        var root = new RootCommand();
        root.AddCommand(build);
        root.AddCommand(clean);
        root.AddCommand(graph);

        return root.Invoke(args);
    }

    static void Build(Env env, bool verbose)
    {
        var curDir = Directory.GetCurrentDirectory();
        var graph = new BuildGraph(curDir, env);
        graph.Traverse(node => {
            var buildDir = env.MirrorSourceToBuild(node.Path);
            if (node.Project.type == ProjectSpec.Type.ext)
            {
                BuildExternal(node, env, verbose);
                return;
            }

            var srcDir = env.GetAbsoluteSourcePath(node.Path);
            Fs.CreateSymbolicLink(Path.Combine(buildDir, "_"), srcDir);

            Prepare(node, env);
            RunExternal("cmake", ". -DCMAKE_BUILD_TYPE=Release", buildDir, verbose);
            RunExternal("cmake", "--build .", node.BuildPath, verbose);
            if (node.Project.type == ProjectSpec.Type.exe)
            {
                // todo problems on Windows? project.name.exe
                var target = Path.Combine(node.BuildPath, $"{node.Project.name}");
                env.LinkArtifacts(target, node.SourcePath);
            }
        });

        PrintOk();
    }

    static void Clean(Env env)
    {
        var buildRoot = env.BuildRoot;
        if (Directory.Exists(buildRoot))
        {
            Directory.Delete(buildRoot, true);
        }

        var srcRoot = env.GetSourceRoot();
        Fs.TraverseDirectories(srcRoot, (dir) =>
        {
            var artifactsDir = Path.Combine(dir, env.ArtifactDir);
            if (Directory.Exists(artifactsDir))
            {
                Directory.Delete(artifactsDir, true);
            }

            return true;
        });
    }

    static void Graph(Env env)
    {
        var curDir = Directory.GetCurrentDirectory();
        var graph = new BuildGraph(curDir, env);
        graph.Traverse(node => {
            Console.WriteLine(node.Path);
        });
    }

    static void BuildExternal(BuildNode node, Env env, bool verbose)
    {
        switch (node.Project.vendor)
        {
            case ProjectSpec.Vendor.conan:
                BuildConan(node, env, verbose);
                break;

            case ProjectSpec.Vendor.cppget:
                BuildCppGet(node, env, verbose);
                break;
            default:
                throw new Exception($"Unknown external vendor: {node.Project.vendor}");
        }
    }

    static void BuildConan(BuildNode node, Env env, bool verbose)
    {
        var packageName = node.Project.name!;
        var packageVersion = node.Project.version!;

        {
            using var writer = File.CreateText($"{node.BuildPath}/conanfile.txt");
            var conanfile = ConanTemplate.Compose(new ConanTemplate.Context()
            {
                Package = packageName,
                Version = packageVersion,
            });

            writer.WriteLine(conanfile);
        }

        RunExternal("conan", "install . --build=missing", node.BuildPath, verbose);
        var (output, _) = RunExternal("conan", $"cache path {packageName}/{packageVersion}", node.BuildPath, false);
        var srcPath = Path.Combine(Path.GetDirectoryName(output.Trim())!, "s", "src");
        env.LinkArtifacts(srcPath, node.SourcePath);
    }

    static void BuildCppGet(BuildNode node, Env env, bool verbose)
    {
        using var writer = File.CreateText(node.CMakeIncludePath);
        var cmake = CppGetTemplate.Compose(new CppGetTemplate.Context()
        {
            Package = node.Project.name!,
            Version = node.Project.version!,
            Url = node.Project.url!
        });

        writer.WriteLine(cmake);
    }

    static void PrintOk()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Ok");
        Console.ResetColor();
    }

    static void PrintFail(string message)
    {
        Console.WriteLine(message);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("Failed");
        Console.ResetColor();
    }

    static List<string> CollectProjectSubdirs(string srcDir, bool top, Env env)
    {
        var result = new List<string>(){srcDir};
        Fs.TraverseDirectories(srcDir, (path) => {
            if (path == srcDir)
            {
                return true;
            }

            if (Fs.IsSymbolicLink(path)) {
                // does not follow symlinks
                return false;
            }

            if (!env.IsProjectRootDir(path))
            {
                result.Add(path);
                return true;
            }

            return false;
        });

        return result;
    }

    static void Prepare(BuildNode node, Env env)
    {
        using var writer = File.CreateText($"{node.BuildPath}/CMakeLists.txt");
        string[] srcExts = {"*.cpp", "*.c"};
        var srcPaths = CollectProjectSubdirs(node.SourcePath, true, env);
        List<string> srcPathExts = new List<string>();
        foreach (var srcPath in srcPaths)
        {
            var relSrcPath = Path.GetRelativePath(node.SourcePath, srcPath);
            foreach (var srcExt in srcExts)
            {
                srcPathExts.Add(Path.Combine(relSrcPath, srcExt));
            }
        }

        var srcSep = "\n    _/";
        var context = new CMakeTemplate.Context
        {
            SrcRoot = env.SourceRoot,
            BuildRoot = env.BuildRoot,
            SrcPaths = srcSep + string.Join(srcSep, srcPathExts) + "\n",
            ProjectName = node.Project.name
        };

        /// todo: expand to dll
        if (node.Project.type == ProjectSpec.Type.exe)
        {
            var libNames = new List<string>();
            var libPaths = new List<string>();
            var packageNames = new List<string>();
            var packagePaths = new List<string>();
            var fetchNames = new List<string>();
            var fetchPaths = new List<string>();
            foreach (var depNode in node.CollectTransitiveLibraryDependers())
            {
                if (depNode.Project.type == ProjectSpec.Type.ext)
                {
                    if (File.Exists(depNode.CMakeIncludePath))
                    {
                        fetchNames.Add(depNode.Name);
                        fetchPaths.Add(depNode.CMakeIncludePath);
                    }
                    else
                    {
                        packageNames.Add(depNode.Alias);
                        packagePaths.Add(depNode.BuildPath);
                    }
                } else
                {
                    libNames.Add(depNode.Name);
                    libPaths.Add(depNode.BuildPath);
                }
            }

            context.DepLibNames = string.Join(" ", libNames);
            context.DepLibPaths = string.Join(" ", libPaths);
            context.DepPackageNames = string.Join(" ", packageNames);
            context.DepPackagePaths = string.Join(" ", packagePaths);
            context.DepFetchNames = string.Join(" ", fetchNames);
            context.DepFetchPaths = string.Join(" ", fetchPaths);
        }

        switch (node.Project.type)
        {
            case ProjectSpec.Type.exe:
                writer.WriteLine(CMakeTemplate.ComposeExe(context));
                break;
            case ProjectSpec.Type.lib:
                writer.WriteLine(CMakeTemplate.ComposeLib(context));
                break;
            default:
                throw new Exception($"Unknown type: {node.Project.type}");
        }
    }

    static (string, string) RunExternal(string cmd, string args, string wd, bool verbose)
    {
        ProcessStartInfo external = new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = args,
            WorkingDirectory = wd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var process = Process.Start(external))
        {
            var rawCmd = cmd + " " + string.Join(' ', args);
            if (process == null)
            {
                throw new Exception($"Bad cmd: {rawCmd}, working dir: {wd}");
            }

            process.WaitForExit();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (verbose)
            {
                Console.WriteLine($"Running '{rawCmd}' from '{wd}'");
                Console.WriteLine(output);
            }

            if (process.ExitCode != 0) {
                Console.WriteLine(error);
                throw new Exception($"Failed to run '{rawCmd}' from dir {wd}");
            }

            return (output, error);
        }
    }
}

